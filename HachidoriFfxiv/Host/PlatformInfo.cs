using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HachidoriFfxiv;

/// <summary>Underlying host operating system, including when running FFXIV/Dalamud through Wine or Proton.</summary>
public enum HostOsKind
{
    Windows,
    Linux,
    MacOs,
    Unknown,
}

/// <summary>
/// Detects whether FFXIV + Dalamud is running on native Windows, Linux (XIVLauncher.Core / Wine / Proton),
/// Steam Deck (SteamOS / Gamescope), or macOS (XIV on Mac / CrossOver), and exposes capability flags so
/// Windows-only features (WebView2 + DirectComposition, per-process WASAPI loopback) fall back cleanly.
/// </summary>
public sealed class PlatformInfo
{
    private static readonly Lazy<PlatformInfo> LazyCurrent = new(DetectLive);

    public static PlatformInfo Current => LazyCurrent.Value;

    public PlatformInfo(
        bool isWine,
        string? wineVersion,
        HostOsKind hostOs,
        bool isSteamDeck,
        bool isGamescope,
        bool isDirectCompositionAvailable,
        Version osVersion)
    {
        IsWine = isWine;
        WineVersion = wineVersion;
        HostOs = hostOs;
        IsSteamDeck = isSteamDeck;
        IsGamescope = isGamescope;
        IsDirectCompositionAvailable = isDirectCompositionAvailable;
        OsVersion = osVersion;
    }

    public bool IsWine { get; }
    public string? WineVersion { get; }
    public HostOsKind HostOs { get; }
    public bool IsSteamDeck { get; }
    public bool IsGamescope { get; }
    public bool IsDirectCompositionAvailable { get; }
    public Version OsVersion { get; }

    public bool IsNativeWindows => !IsWine && HostOs == HostOsKind.Windows;

    /// <summary>
    /// True when the transparent, permanently click-through DirectComposition WebView2 overlay is supported.
    /// Wine/Proton/CrossOver and Gamescope do not implement DirectComposition windowless WebView2 overlays.
    /// </summary>
    public bool SupportsWebView2Overlay => IsNativeWindows && IsDirectCompositionAvailable;

    /// <summary>
    /// True when Windows 10 2004+ per-process WASAPI loopback (<c>VAD\Process_Loopback</c>) is expected to work.
    /// Other environments automatically fall back to standard WASAPI system-output loopback.
    /// </summary>
    public bool SupportsProcessAudioLoopback => IsNativeWindows && OsVersion >= new Version(10, 0, 19041);

    /// <summary>True when running under any non-standard or compatibility environment (Wine, Linux, Steam Deck, macOS, or Win10 &lt; 2004).</summary>
    public bool HasCompatibilityFallbacks => !SupportsWebView2Overlay || !SupportsProcessAudioLoopback;

    public string DisplayName
    {
        get
        {
            if (IsSteamDeck)
                return IsGamescope ? "Steam Deck (SteamOS · Gaming Mode)" : "Steam Deck (SteamOS / Proton)";
            if (IsWine)
            {
                var ver = string.IsNullOrWhiteSpace(WineVersion) ? string.Empty : $" {WineVersion}";
                return HostOs switch
                {
                    HostOsKind.MacOs => $"macOS (XIV on Mac / Wine{ver})",
                    HostOsKind.Linux => $"Linux (Wine / Proton{ver})",
                    _ => $"Wine{ver}",
                };
            }

            if (OsVersion.Major >= 10 && OsVersion.Build >= 22000)
                return "Windows 11";
            if (OsVersion.Major >= 10)
                return OsVersion.Build >= 19041 ? "Windows 10" : $"Windows 10 (build {OsVersion.Build})";
            return $"Windows ({OsVersion.Major}.{OsVersion.Minor})";
        }
    }

    /// <summary>Pure classification helper used by <see cref="DetectLive"/> and unit tests.</summary>
    public static PlatformInfo Classify(
        string? wineVersion,
        string? wineHostSysname,
        Func<string, string?> getEnv,
        bool hasMacSystemPlist,
        string? linuxOsRelease,
        bool hasDirectComposition,
        Version osVersion)
    {
        var envWine = !string.IsNullOrWhiteSpace(getEnv("XL_WINEONLINUX"))
            || !string.IsNullOrWhiteSpace(getEnv("WINEPREFIX"))
            || !string.IsNullOrWhiteSpace(getEnv("PROTON_VERSION"))
            || !string.IsNullOrWhiteSpace(getEnv("STEAM_COMPAT_DATA_PATH"));
        var isWine = !string.IsNullOrWhiteSpace(wineVersion) || envWine;

        var hostOs = HostOsKind.Windows;
        if (isWine)
        {
            if (string.Equals(wineHostSysname, "Darwin", StringComparison.OrdinalIgnoreCase) || hasMacSystemPlist)
                hostOs = HostOsKind.MacOs;
            else if (string.Equals(wineHostSysname, "Linux", StringComparison.OrdinalIgnoreCase)
                     || !string.IsNullOrWhiteSpace(linuxOsRelease)
                     || !string.IsNullOrWhiteSpace(getEnv("XL_WINEONLINUX"))
                     || !string.IsNullOrWhiteSpace(getEnv("PROTON_VERSION"))
                     || !string.IsNullOrWhiteSpace(getEnv("STEAM_COMPAT_DATA_PATH")))
                hostOs = HostOsKind.Linux;
            else
                hostOs = HostOsKind.Unknown;
        }

        var isGamescope = !string.IsNullOrWhiteSpace(getEnv("GAMESCOPE_WAYLAND_DISPLAY"))
            || string.Equals(getEnv("STEAM_GAMEPADUI"), "1", StringComparison.Ordinal)
            || (getEnv("XDG_CURRENT_DESKTOP")?.Contains("gamescope", StringComparison.OrdinalIgnoreCase) == true);

        var osReleaseIsSteamDeck = !string.IsNullOrWhiteSpace(linuxOsRelease)
            && (linuxOsRelease.Contains("ID=steamos", StringComparison.OrdinalIgnoreCase)
                || linuxOsRelease.Contains("ID=\"steamos\"", StringComparison.OrdinalIgnoreCase)
                || linuxOsRelease.Contains("VARIANT_ID=steamdeck", StringComparison.OrdinalIgnoreCase)
                || linuxOsRelease.Contains("VARIANT_ID=\"steamdeck\"", StringComparison.OrdinalIgnoreCase));

        var isSteamDeck = isWine && hostOs != HostOsKind.MacOs && (
            string.Equals(getEnv("SteamDeck"), "1", StringComparison.Ordinal)
            || string.Equals(getEnv("STEAM_DECK"), "1", StringComparison.Ordinal)
            || string.Equals(getEnv("SteamOS"), "1", StringComparison.Ordinal)
            || osReleaseIsSteamDeck
            || isGamescope);

        if (isSteamDeck)
            hostOs = HostOsKind.Linux;

        return new PlatformInfo(
            isWine: isWine,
            wineVersion: string.IsNullOrWhiteSpace(wineVersion) ? null : wineVersion.Trim(),
            hostOs: hostOs,
            isSteamDeck: isSteamDeck,
            isGamescope: isGamescope,
            isDirectCompositionAvailable: !isWine && hasDirectComposition,
            osVersion: osVersion);
    }

    private static PlatformInfo DetectLive()
    {
        var (wineVersion, wineHostSysname) = QueryWineFromNtdll();
        var maybeWine = !string.IsNullOrWhiteSpace(wineVersion)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XL_WINEONLINUX"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINEPREFIX"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROTON_VERSION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH"));

        var hasMacPlist = false;
        string? osRelease = null;
        if (maybeWine)
        {
            try { hasMacPlist = File.Exists(@"Z:\System\Library\CoreServices\SystemVersion.plist"); }
            catch { /* best effort */ }

            if (!hasMacPlist)
            {
                try
                {
                    if (File.Exists(@"Z:\etc\os-release"))
                    {
                        var text = File.ReadAllText(@"Z:\etc\os-release");
                        osRelease = text.Length > 4096 ? text[..4096] : text;
                    }
                }
                catch { /* best effort */ }
            }
        }

        var hasDcomp = !maybeWine && ProbeDirectComposition();
        return Classify(
            wineVersion,
            wineHostSysname,
            Environment.GetEnvironmentVariable,
            hasMacPlist,
            osRelease,
            hasDcomp,
            Environment.OSVersion.Version);
    }

    private static unsafe (string? Version, string? HostSysname) QueryWineFromNtdll()
    {
        try
        {
            if (!NativeLibrary.TryLoad("ntdll.dll", out var ntdll))
                return (null, null);

            string? version = null;
            string? sysname = null;
            if (NativeLibrary.TryGetExport(ntdll, "wine_get_version", out var getVerPtr))
            {
                var getVer = (delegate* unmanaged[Cdecl]<nint>)getVerPtr;
                var ptr = getVer();
                if (ptr != 0)
                    version = Marshal.PtrToStringAnsi(ptr);
            }

            if (version is not null && NativeLibrary.TryGetExport(ntdll, "wine_get_host_version", out var getHostPtr))
            {
                var getHost = (delegate* unmanaged[Cdecl]<nint*, nint*, void>)getHostPtr;
                nint sysPtr = 0, relPtr = 0;
                getHost(&sysPtr, &relPtr);
                if (sysPtr != 0)
                    sysname = Marshal.PtrToStringAnsi(sysPtr);
            }

            return (version, sysname);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool ProbeDirectComposition()
    {
        try
        {
            if (!NativeLibrary.TryLoad("dcomp.dll", out var dcomp))
                return false;
            try
            {
                return NativeLibrary.TryGetExport(dcomp, "DCompositionCreateDevice", out _);
            }
            finally
            {
                NativeLibrary.Free(dcomp);
            }
        }
        catch
        {
            return false;
        }
    }
}
