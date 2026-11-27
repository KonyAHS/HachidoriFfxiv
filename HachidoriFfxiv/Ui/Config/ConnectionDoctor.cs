using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.WebView;

namespace HachidoriFfxiv.Ui;

public enum DoctorSeverity
{
    Ok,
    Info,
    Warn,
    Error,
}

public sealed record DoctorCheck(
    string Id,
    string Label,
    DoctorSeverity Severity,
    string Detail,
    string? ActionLabel = null,
    string? ActionId = null);

/// <summary>
/// Live multi-hop diagnostic prober for the Setup tab ("Connection Doctor").
/// Adapts its checks to the user's selected <see cref="DictionarySource"/> so irrelevant
/// components stay out of the way.
/// </summary>
public sealed class ConnectionDoctor : IDisposable
{
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Offline.OfflineMode? offline;
    private readonly MediaMiner media;
    private readonly WebViewPopupController? webView;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private Task? activeRun;
    private DateTime lastRunUtc = DateTime.MinValue;
    private IReadOnlyList<DoctorCheck> checks = [];

    public ConnectionDoctor(
        Configuration config,
        HachidoriService service,
        Offline.OfflineMode? offline,
        MediaMiner media,
        WebViewPopupController? webView)
    {
        this.config = config;
        this.service = service;
        this.offline = offline;
        this.media = media;
        this.webView = webView;
    }

    public bool Running => activeRun is { IsCompleted: false };
    public DateTime LastRunUtc => lastRunUtc;
    public IReadOnlyList<DoctorCheck> Checks => checks;

    public bool AnkiConnectOk { get; private set; }
    public string? AnkiConnectVersion { get; private set; }
    public bool RelayPortOpen { get; private set; }

    public DictionarySource EffectiveSource()
    {
        if (config.DictionarySource != DictionarySource.Auto)
            return config.DictionarySource;
        if (service.Link.Connected)
            return DictionarySource.HachidoriBrowser;
        if (offline is { Store.HasDictionaries: true } off)
        {
            var anySynced = false;
            foreach (var d in off.Store.Manifest.Dictionaries)
            {
                if (!string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase))
                {
                    anySynced = true;
                    break;
                }
            }
            return anySynced ? DictionarySource.HachidoriBrowser : DictionarySource.BuiltIn;
        }
        return DictionarySource.BuiltIn;
    }

    public void EnsureFresh(TimeSpan? maxAge = null)
    {
        var age = maxAge ?? TimeSpan.FromSeconds(15);
        if (!Running && DateTime.UtcNow - lastRunUtc > age)
            RunNow();
    }

    public void RunNow()
    {
        if (Running)
            return;
        activeRun = Task.Run(ProbeAllAsync);
    }

    private async Task ProbeAllAsync()
    {
        var results = new List<DoctorCheck>();
        var source = EffectiveSource();
        try
        {
            // 1. Dictionaries
            if (source == DictionarySource.HachidoriBrowser)
            {
                if (service.Link.Connected)
                {
                    var count = service.Link.DictionaryCount;
                    results.Add(count > 0
                        ? new("dicts", "Dictionaries", DoctorSeverity.Ok, $"Hachidori v{service.Link.HostVersion} ({count} dicts)")
                        : new("dicts", "Dictionaries", DoctorSeverity.Warn, "Connected to Hachidori, but 0 dictionaries are imported."));
                }
                else if (service.UsingOffline && offline is { } off)
                {
                    var count = off.Store.Manifest.Dictionaries.Count;
                    results.Add(new("dicts", "Dictionaries", DoctorSeverity.Ok, $"Offline copy active ({count} synced dicts)"));
                }
                else
                {
                    results.Add(new("dicts", "Dictionaries", DoctorSeverity.Error,
                        "Hachidori extension is not linked yet.", "Reconnect", "reconnect"));
                }
            }
            else
            {
                if (offline is { Ready: true } off)
                {
                    var count = off.Store.Manifest.Dictionaries.Count;
                    results.Add(new("dicts", "Dictionaries", DoctorSeverity.Ok, $"Built-in engine ready ({count} dict{(count == 1 ? "" : "s")})"));
                }
                else
                {
                    results.Add(new("dicts", "Dictionaries", DoctorSeverity.Error,
                        "No dictionaries imported yet. Import a Yomitan .zip or install JMdict above.",
                        "Import .zip", "import-zip"));
                }
            }

            // 2. Hachidori Relay — only shown when using the Hachidori Browser Extension source
            if (source == DictionarySource.HachidoriBrowser)
            {
                RelayPortOpen = await ProbeTcpPortAsync(config.RelayHost, config.RelayPort).ConfigureAwait(false);
                if (service.Link.Connected)
                    results.Add(new("relay", "Hachidori Relay", DoctorSeverity.Ok, $"Linked on {config.RelayHost}:{config.RelayPort}"));
                else if (RelayPortOpen)
                    results.Add(new("relay", "Hachidori Relay", DoctorSeverity.Warn,
                        "Relay is listening; open Hachidori in your browser and enable Settings → Sharing.",
                        "Reconnect", "reconnect"));
                else
                    results.Add(new("relay", "Hachidori Relay", DoctorSeverity.Warn,
                        $"Not listening on :{config.RelayPort}. Open Anki with the Hachidori Relay add-on.",
                        "Reconnect", "reconnect"));
            }

            // 3. AnkiConnect (:8765)
            AnkiConnectOk = false;
            AnkiConnectVersion = null;
            try
            {
                var ver = await media.Anki.InvokeAsync("version", null, retryOnTransportError: false).ConfigureAwait(false);
                AnkiConnectOk = ver is not null;
                AnkiConnectVersion = ver?.ToString();
                results.Add(new("anki", "AnkiConnect", DoctorSeverity.Ok,
                    $"Connected (v{AnkiConnectVersion}) at {config.AnkiConnectUrl}"));
            }
            catch (Exception)
            {
                var neededForMining = config.SetupStyle != SetupStyle.KeepGsm;
                results.Add(new("anki", "AnkiConnect", neededForMining ? DoctorSeverity.Warn : DoctorSeverity.Info,
                    $"Not reachable at {config.AnkiConnectUrl}. Install AnkiConnect (2055492159) in Anki to mine cards.",
                    "Get AnkiConnect", "open-ankiconnect"));
            }

            // 4. Anki Template readiness (when AnkiConnect is reachable & not using GSM for mining)
            if (AnkiConnectOk && config.SetupStyle != SetupStyle.KeepGsm)
            {
                try
                {
                    var status = await service.AnkiStatusAsync().ConfigureAwait(false);
                    if (status.Available)
                        results.Add(new("template", "Anki Template", DoctorSeverity.Ok, "Deck, note type, and fields are valid."));
                    else
                        results.Add(new("template", "Anki Template", DoctorSeverity.Warn,
                            status.Error ?? "Configure your deck and note type in Mining.",
                            "Map Fields", "open-mining"));
                }
                catch (Exception ex)
                {
                    results.Add(new("template", "Anki Template", DoctorSeverity.Warn,
                        $"Template check failed: {ex.Message}", "Map Fields", "open-mining"));
                }
            }

            // 5. FFmpeg (only surfaced when animated AVIF/WebP is enabled or already detected)
            var ffmpeg = media.FfmpegPath;
            if (ffmpeg is not null)
            {
                var caps = Ffmpeg.Capabilities(ffmpeg);
                var fmt = caps.Av1 is not null ? "AVIF + WebP" : caps.Webp ? "WebP" : "basic";
                results.Add(new("ffmpeg", "FFmpeg", DoctorSeverity.Ok, $"Detected ({fmt})"));
            }
            else if (config.CaptureGif && config.AnimationFormat != AnimationFormat.Gif)
            {
                results.Add(new("ffmpeg", "FFmpeg", DoctorSeverity.Warn,
                    "Not found — animated clips will fall back to GIF.",
                    "Download FFmpeg", "download-ffmpeg"));
            }

            // 6. Popup Renderer
            if (webView is { PlatformSupported: true })
            {
                if (!config.UseWebViewPopup)
                    results.Add(new("renderer", "Popup Renderer", DoctorSeverity.Info, "Built-in ImGui renderer"));
                else if (webView.Active)
                    results.Add(new("renderer", "Popup Renderer", DoctorSeverity.Ok, $"Hachidori Web v{webView.LoadedVersion}"));
                else if (!webView.RuntimeAvailable)
                    results.Add(new("renderer", "Popup Renderer", DoctorSeverity.Warn, "WebView2 Runtime missing — using built-in renderer."));
                else
                    results.Add(new("renderer", "Popup Renderer", DoctorSeverity.Info, $"Built-in fallback ({webView.StatusText}).", "Sync Web UI", "sync-webview"));
            }
            else
            {
                results.Add(new("renderer", "Popup Renderer", DoctorSeverity.Info, $"Built-in ImGui renderer ({PlatformInfo.Current.DisplayName})"));
            }
        }
        finally
        {
            checks = results;
            lastRunUtc = DateTime.UtcNow;
        }
    }

    private static async Task<bool> ProbeTcpPortAsync(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            await tcp.ConnectAsync(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host, port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => http.Dispose();
}
