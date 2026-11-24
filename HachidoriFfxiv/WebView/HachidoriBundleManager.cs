using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.WebView;

/// <summary>One theme advertised by the active Hachidori Web bundle's <c>vendor/themes/index.json</c>.</summary>
public sealed record HachidoriThemeDescriptor(
    string Slug,
    string Name,
    string Description,
    IReadOnlyList<(string Id, string Name)> Variants);

/// <summary>
/// Manages the runtime-updatable Hachidori Web frontend bundle cached under
/// <c>&lt;pluginConfigDir&gt;/hachidori-web/</c>. Keeps the embedded WebView2 popup in step
/// with Bee-san's Hachidori releases and themes automatically without rebuilding the plugin:
/// <list type="number">
///   <item>Uses a cached bundle matching <c>HostVersion</c> when present and healthy.</item>
///   <item>Checks a user-configured extension folder or auto-detected local Chrome/Edge/Brave Hachidori install.</item>
///   <item>Downloads the version-matched <c>*-chrome.zip</c> from <c>bee-san/hachidori</c> GitHub Releases.</item>
///   <item>Falls back / rolls back to <c>lastGoodVersion</c> if a newer version fails to download or boot.</item>
/// </list>
/// </summary>
public sealed class HachidoriBundleManager : IDisposable
{
    public const string DefaultRepository = "bee-san/hachidori";
    private const long MaxExtractFileBytes = 8 * 1024 * 1024;
    private const int MaxExtractFiles = 1024;

    private static readonly string[] RequiredFiles =
    [
        "manifest.json",
        "content.js",
        "theme-host.js",
        "render/popup.js",
        "render/glossary.js",
        "render/reader.css",
        "vendor/themes/index.json",
    ];

    private readonly string rootDir;
    private readonly string versionsDir;
    private readonly string statePath;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim syncLock = new(1, 1);

    private BundleState state = new();
    private IReadOnlyList<HachidoriThemeDescriptor> cachedThemes = [];
    private IReadOnlyList<string> cachedDefaultPalettes = [];
    private string? catalogLoadedForPath;

    public HachidoriBundleManager(string rootDirectory, HttpClient? httpClient = null)
    {
        rootDir = rootDirectory;
        versionsDir = Path.Combine(rootDir, "versions");
        statePath = Path.Combine(rootDir, "state.json");
        if (httpClient is not null)
        {
            http = httpClient;
            ownsHttp = false;
        }
        else
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HachidoriFfxiv/1.0");
            ownsHttp = true;
        }

        LoadState();
        ResolveInitialActive();
    }

    public string? ActiveVersion { get; private set; }
    public string? ActivePath { get; private set; }
    public string ActiveSource { get; private set; } = string.Empty;
    public string? LastGoodVersion => state.LastGoodVersion;
    public DateTime? SyncedAt => state.SyncedAt;
    public bool Syncing { get; private set; }
    public string? LastError { get; private set; }
    public bool Ready => ActivePath is not null && ValidateBundle(ActivePath);

    /// <summary>Raised whenever the active bundle path or version changes.</summary>
    public event Action? BundleChanged;

    public IReadOnlyList<HachidoriThemeDescriptor> Themes
    {
        get
        {
            EnsureCatalogLoaded();
            return cachedThemes;
        }
    }

    public IReadOnlyList<string> DefaultPalettes
    {
        get
        {
            EnsureCatalogLoaded();
            return cachedDefaultPalettes;
        }
    }

    /// <summary>
    /// Ensures a valid Hachidori Web bundle is ready, preferring <paramref name="targetVersion"/>
    /// (typically <c>HachidoriLink.HostVersion</c>). Checks cache, custom path, local Chromium
    /// extension installs, and finally <c>bee-san/hachidori</c> GitHub Releases.
    /// </summary>
    public async Task<bool> EnsureBundleAsync(
        string? targetVersion = null,
        string? customExtensionPath = null,
        bool allowNetwork = true,
        bool force = false,
        CancellationToken ct = default)
    {
        await syncLock.WaitAsync(ct).ConfigureAwait(false);
        Syncing = true;
        try
        {
            LastError = null;
            var wanted = CleanVersion(targetVersion);

            // 1. Custom extension folder override (explicit user path).
            if (!string.IsNullOrWhiteSpace(customExtensionPath))
            {
                if (TryInstallFromLocalDirectory(customExtensionPath.Trim(), wanted, force, out var installedVer, out var installedDir))
                {
                    Activate(installedVer, installedDir, "custom-path");
                    return true;
                }
            }

            // 2. Already cached version matching targetVersion (and not marked broken).
            if (!force && wanted is not null && !IsFailed(wanted))
            {
                var cachedDir = VersionDir(wanted);
                if (ValidateBundle(cachedDir))
                {
                    Activate(wanted, cachedDir, state.VersionSources.GetValueOrDefault(wanted, "cached"));
                    return true;
                }
            }

            // 3. Auto-detect local Chrome/Edge/Brave Hachidori extension folder (instant, zero network).
            if (TryDetectLocalChromiumExtension(wanted, out var localDir, out var localVer)
                && !IsFailed(localVer)
                && TryInstallFromLocalDirectory(localDir, wanted: null, force, out var copiedVer, out var copiedDir))
            {
                Activate(copiedVer, copiedDir, "local-browser");
                return true;
            }

            // 4. Download matching release archive from bee-san/hachidori on GitHub Releases.
            if (allowNetwork)
            {
                try
                {
                    var (dlVer, dlDir) = await DownloadFromGitHubAsync(wanted, ct).ConfigureAwait(false);
                    if (!IsFailed(dlVer) || force)
                    {
                        state.FailedVersions.Remove(dlVer);
                        Activate(dlVer, dlDir, "github-release");
                        return true;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LastError = $"Could not sync Hachidori Web bundle{(wanted is not null ? $" v{wanted}" : "")}: {ex.Message}";
                    DiagnosticLog.Write("webview-bundle", LastError);
                }
            }

            // 5. Fallback to last-known-good or any healthy cached version.
            if (TryActivateFallback())
                return true;

            LastError ??= "No Hachidori Web bundle is cached yet. Connect to the internet or install Hachidori in Chrome to sync automatically.";
            return false;
        }
        finally
        {
            Syncing = false;
            syncLock.Release();
        }
    }

    /// <summary>Installs a Hachidori Web bundle directly from a local extension directory (used by sync and tests).</summary>
    public bool InstallFromDirectory(string sourceDirectory, string sourceLabel = "local")
    {
        syncLock.Wait();
        try
        {
            if (!TryInstallFromLocalDirectory(sourceDirectory, wanted: null, force: true, out var version, out var targetDir))
                return false;
            state.FailedVersions.Remove(version);
            Activate(version, targetDir, sourceLabel);
            return true;
        }
        finally
        {
            syncLock.Release();
        }
    }

    /// <summary>Installs a Hachidori Web bundle from a <c>*-chrome.zip</c> archive stream.</summary>
    public bool InstallFromZip(Stream zipStream, string sourceLabel = "zip")
    {
        syncLock.Wait();
        try
        {
            var (version, targetDir) = ExtractZipToVersions(zipStream, expectedVersion: null);
            state.FailedVersions.Remove(version);
            Activate(version, targetDir, sourceLabel);
            return true;
        }
        finally
        {
            syncLock.Release();
        }
    }

    /// <summary>Marks the active bundle version as healthy (becomes the rollback target).</summary>
    public void MarkBootSuccess(string? version)
    {
        var v = CleanVersion(version) ?? ActiveVersion;
        if (v is null) return;
        state.LastGoodVersion = v;
        state.FailedVersions.Remove(v);
        SaveState();
    }

    /// <summary>Marks a bundle version as broken and rolls back to <see cref="LastGoodVersion"/> if available.</summary>
    public bool MarkBootFailure(string? version, string error)
    {
        var v = CleanVersion(version) ?? ActiveVersion;
        LastError = error;
        if (v is not null)
        {
            state.FailedVersions.Add(v);
            DiagnosticLog.Write("webview-bundle", $"Bundle v{v} failed to boot: {error}");
            SaveState();
        }
        return TryActivateFallback(excludeVersion: v);
    }

    /// <summary>Validates that a folder contains the essential Hachidori reader and theme files.</summary>
    public static bool ValidateBundle(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;
        foreach (var rel in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, rel.Replace('/', Path.DirectorySeparatorChar))))
                return false;
        }
        return ReadManifestVersion(directory) is not null;
    }

    /// <summary>Reads the <c>version</c> field from <c>manifest.json</c> in a Hachidori extension folder.</summary>
    public static string? ReadManifestVersion(string directory)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            return CleanVersion(node?["version"]?.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Filters out heavy engine-only files (WASM binaries, language transform tables, video encoders) not needed by the popup UI.</summary>
    private static bool ShouldIncludeBundleEntry(string relativeUnixPath)
    {
        var path = relativeUnixPath.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.EndsWith('/') || path.Contains("..", StringComparison.Ordinal))
            return false;
        if (path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("vendor/codemirror/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("vendor/mediabunny/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("vendor/kanjidic/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("vendor/kanwa/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("vendor/languages/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/transforms.json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path is "vendor/gifenc.js" or "vendor/lamejs.js" or "vendor/zip.js"
            or "vendor/hoshidicts.mjs" or "vendor/hoshidicts-threaded.mjs" or "vendor/hoshidicts-threaded-idbfs.mjs")
            return false;
        if (path.StartsWith("assets/preview-background", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/screenshot.png", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ------------------------------------------------------------------ private helpers

    private string VersionDir(string version) => Path.Combine(versionsDir, SanitizeVersionFolder(version));

    private static string SanitizeVersionFolder(string version) =>
        Regex.Replace(version, @"[^A-Za-z0-9._-]", "_");

    private static string? CleanVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimStart('v', 'V');
        return s.Length > 0 && s.Length <= 64 ? s : null;
    }

    private bool IsFailed(string version) => state.FailedVersions.Contains(version);

    private void Activate(string version, string path, string source)
    {
        var changed = !string.Equals(ActivePath, path, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(ActiveVersion, version, StringComparison.Ordinal);
        ActiveVersion = version;
        ActivePath = path;
        ActiveSource = source;
        state.ActiveVersion = version;
        state.SyncedAt = DateTime.UtcNow;
        state.VersionSources[version] = source;
        SaveState();
        if (changed)
        {
            catalogLoadedForPath = null;
            DiagnosticLog.Write("webview-bundle", $"Activated Hachidori Web v{version} from {source} ({path})");
            BundleChanged?.Invoke();
        }
    }

    private void ResolveInitialActive()
    {
        foreach (var candidate in new[] { state.ActiveVersion, state.LastGoodVersion })
        {
            if (candidate is null || IsFailed(candidate)) continue;
            var dir = VersionDir(candidate);
            if (ValidateBundle(dir))
            {
                ActiveVersion = candidate;
                ActivePath = dir;
                ActiveSource = state.VersionSources.GetValueOrDefault(candidate, "cached");
                return;
            }
        }
        TryActivateFallback();
    }

    private bool TryActivateFallback(string? excludeVersion = null)
    {
        if (state.LastGoodVersion is { } good && good != excludeVersion && !IsFailed(good))
        {
            var goodDir = VersionDir(good);
            if (ValidateBundle(goodDir))
            {
                Activate(good, goodDir, state.VersionSources.GetValueOrDefault(good, "cached"));
                return true;
            }
        }
        if (!Directory.Exists(versionsDir)) return false;
        foreach (var dir in Directory.GetDirectories(versionsDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var ver = ReadManifestVersion(dir);
            if (ver is null || ver == excludeVersion || IsFailed(ver)) continue;
            if (ValidateBundle(dir))
            {
                Activate(ver, dir, state.VersionSources.GetValueOrDefault(ver, "cached"));
                return true;
            }
        }
        return false;
    }

    private bool TryInstallFromLocalDirectory(string sourceDir, string? wanted, bool force, out string version, out string targetDir)
    {
        version = string.Empty;
        targetDir = string.Empty;
        if (!ValidateBundle(sourceDir)) return false;
        var foundVersion = ReadManifestVersion(sourceDir);
        if (foundVersion is null) return false;
        if (wanted is not null && !string.Equals(foundVersion, wanted, StringComparison.OrdinalIgnoreCase))
            return false;

        version = foundVersion;
        targetDir = VersionDir(foundVersion);
        if (!force && ValidateBundle(targetDir))
            return true;

        Directory.CreateDirectory(versionsDir);
        var staging = Path.Combine(versionsDir, $".staging-{Guid.NewGuid():N}");
        try
        {
            CopyFilteredDirectory(sourceDir, staging);
            if (!ValidateBundle(staging))
                return false;
            SwapDirectory(staging, targetDir);
            return true;
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    private static void CopyFilteredDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var rootFull = Path.GetFullPath(sourceDir);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            if (!ShouldIncludeBundleEntry(rel)) continue;
            var dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static bool TryDetectLocalChromiumExtension(string? wantedVersion, out string bestDir, out string bestVersion)
    {
        bestDir = string.Empty;
        bestVersion = string.Empty;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return false;

        string[] userDataRoots =
        [
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(localAppData, "Vivaldi", "User Data"),
        ];

        var candidates = new List<(string Version, string Dir)>();
        foreach (var root in userDataRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(root)
                    .Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)));
            }
            catch { continue; }

            foreach (var profile in profiles)
            {
                var extensionsRoot = Path.Combine(profile, "Extensions");
                if (!Directory.Exists(extensionsRoot)) continue;
                IEnumerable<string> extDirs;
                try { extDirs = Directory.EnumerateDirectories(extensionsRoot); }
                catch { continue; }

                foreach (var extIdDir in extDirs)
                {
                    IEnumerable<string> verDirs;
                    try { verDirs = Directory.EnumerateDirectories(extIdDir); }
                    catch { continue; }

                    foreach (var verDir in verDirs)
                    {
                        if (!IsHachidoriExtensionDirectory(verDir)) continue;
                        var ver = ReadManifestVersion(verDir);
                        if (ver is null) continue;
                        if (wantedVersion is not null && string.Equals(ver, wantedVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            bestDir = verDir;
                            bestVersion = ver;
                            return true;
                        }
                        candidates.Add((ver, verDir));
                    }
                }
            }
        }

        if (wantedVersion is not null || candidates.Count == 0)
            return false;

        // Pick the highest semantic version found locally.
        var picked = candidates
            .OrderByDescending(c => Version.TryParse(c.Version.Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
            .First();
        bestDir = picked.Dir;
        bestVersion = picked.Version;
        return true;
    }

    private static bool IsHachidoriExtensionDirectory(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "theme-host.js"))
            || !File.Exists(Path.Combine(dir, "render", "popup.js"))
            || !File.Exists(Path.Combine(dir, "vendor", "themes", "index.json")))
            return false;
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            var text = File.ReadAllText(manifestPath, Encoding.UTF8);
            return text.Contains("\"Hachidori\"", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private async Task<(string Version, string Dir)> DownloadFromGitHubAsync(string? wantedVersion, CancellationToken ct)
    {
        var urls = wantedVersion is not null
            ? new[]
            {
                $"https://api.github.com/repos/{DefaultRepository}/releases/tags/{Uri.EscapeDataString(wantedVersion)}",
                $"https://api.github.com/repos/{DefaultRepository}/releases/tags/v{Uri.EscapeDataString(wantedVersion)}",
                $"https://api.github.com/repos/{DefaultRepository}/releases/latest",
            }
            : new[] { $"https://api.github.com/repos/{DefaultRepository}/releases/latest" };

        JsonObject? release = null;
        Exception? lastEx = null;
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (JsonNode.Parse(text) is JsonObject obj)
                {
                    release = obj;
                    break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastEx = ex;
            }
        }

        if (release is null)
            throw new InvalidOperationException("Could not query Hachidori releases on GitHub.", lastEx);

        var assets = release["assets"] as JsonArray ?? [];
        var asset = assets.OfType<JsonObject>()
            .FirstOrDefault(a =>
            {
                var name = a["name"]?.ToString() ?? string.Empty;
                return name.EndsWith("-chrome.zip", StringComparison.OrdinalIgnoreCase);
            })
            ?? assets.OfType<JsonObject>().FirstOrDefault(a =>
            {
                var name = a["name"]?.ToString() ?? string.Empty;
                return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                       && !name.Contains("source", StringComparison.OrdinalIgnoreCase)
                       && !name.Contains("firefox", StringComparison.OrdinalIgnoreCase);
            });

        var downloadUrl = asset?["browser_download_url"]?.ToString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("Hachidori GitHub release has no Chrome bundle archive.");

        DiagnosticLog.Write("webview-bundle", $"Downloading Hachidori Web bundle from {downloadUrl}");
        using var zipResponse = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        zipResponse.EnsureSuccessStatusCode();
        using var ms = new MemoryStream();
        await zipResponse.Content.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        return ExtractZipToVersions(ms, CleanVersion(release["tag_name"]?.ToString()));
    }

    private (string Version, string Dir) ExtractZipToVersions(Stream zipStream, string? expectedVersion)
    {
        Directory.CreateDirectory(versionsDir);
        var staging = Path.Combine(versionsDir, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                // Detect optional common top-level wrapper folder (e.g. "extension/").
                var prefix = DetectCommonPrefix(archive);
                var count = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var normalized = entry.FullName.Replace('\\', '/');
                    if (prefix.Length > 0 && normalized.StartsWith(prefix, StringComparison.Ordinal))
                        normalized = normalized[prefix.Length..];
                    if (!ShouldIncludeBundleEntry(normalized)) continue;
                    if (++count > MaxExtractFiles)
                        throw new InvalidOperationException("Hachidori bundle archive exceeds file count limit.");
                    if (entry.Length > MaxExtractFileBytes)
                        continue;

                    var target = Path.GetFullPath(Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive entry escapes extraction directory.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = entry.Open();
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                }
            }

            if (!ValidateBundle(staging))
                throw new InvalidOperationException("Extracted Hachidori archive is missing required popup or theme files.");

            var version = ReadManifestVersion(staging) ?? expectedVersion
                ?? throw new InvalidOperationException("Could not determine Hachidori version from manifest.json.");
            var finalDir = VersionDir(version);
            SwapDirectory(staging, finalDir);
            return (version, finalDir);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    private static string DetectCommonPrefix(ZipArchive archive)
    {
        var manifest = archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifest is null) return string.Empty;
        var norm = manifest.FullName.Replace('\\', '/');
        var idx = norm.LastIndexOf('/');
        return idx >= 0 ? norm[..(idx + 1)] : string.Empty;
    }

    private static void SwapDirectory(string staging, string target)
    {
        var backup = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
        if (Directory.Exists(target))
            Directory.Move(target, backup);
        try
        {
            Directory.Move(staging, target);
            TryDeleteDir(backup);
        }
        catch
        {
            if (Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            throw;
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    private void EnsureCatalogLoaded()
    {
        if (ActivePath is null || string.Equals(catalogLoadedForPath, ActivePath, StringComparison.OrdinalIgnoreCase))
            return;
        catalogLoadedForPath = ActivePath;

        var list = new List<HachidoriThemeDescriptor>();
        try
        {
            var indexPath = Path.Combine(ActivePath, "vendor", "themes", "index.json");
            if (File.Exists(indexPath) && JsonNode.Parse(File.ReadAllText(indexPath, Encoding.UTF8))?["themes"] is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                {
                    var slug = item["slug"]?.ToString() ?? string.Empty;
                    if (slug.Length == 0) continue;
                    var name = item["name"]?.ToString() ?? slug;
                    var desc = item["description"]?.ToString() ?? string.Empty;
                    var variants = new List<(string Id, string Name)>();
                    if (item["variants"] is JsonArray varArr)
                    {
                        foreach (var v in varArr.OfType<JsonObject>())
                        {
                            var vid = v["id"]?.ToString() ?? string.Empty;
                            if (vid.Length > 0)
                                variants.Add((vid, v["name"]?.ToString() ?? vid));
                        }
                    }
                    list.Add(new HachidoriThemeDescriptor(slug, name, desc, variants));
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("webview-bundle", $"Could not parse vendor/themes/index.json: {ex.Message}");
        }
        cachedThemes = list;

        var palettes = new List<string>();
        try
        {
            var readerCssPath = Path.Combine(ActivePath, "render", "reader.css");
            if (File.Exists(readerCssPath))
            {
                var css = File.ReadAllText(readerCssPath, Encoding.UTF8);
                palettes.AddRange(Regex.Matches(css, @":host\(\[data-hoshidicts-theme=""([a-z0-9-]+)""\]\)")
                    .Select(m => m.Groups[1].Value).Distinct());
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("webview-bundle", $"Could not read palettes from render/reader.css: {ex.Message}");
        }
        cachedDefaultPalettes = palettes;
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(statePath))
            {
                var parsed = JsonSerializer.Deserialize<BundleState>(File.ReadAllText(statePath, Encoding.UTF8));
                if (parsed is not null) state = parsed;
            }
        }
        catch
        {
            state = new BundleState();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(rootDir);
            var tmp = statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(tmp, statePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
        syncLock.Dispose();
    }

    private sealed class BundleState
    {
        public string? ActiveVersion { get; set; }
        public string? LastGoodVersion { get; set; }
        public DateTime? SyncedAt { get; set; }
        public Dictionary<string, string> VersionSources { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> FailedVersions { get; set; } = new(StringComparer.Ordinal);
    }
}
