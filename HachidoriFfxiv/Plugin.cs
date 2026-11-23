using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HachidoriFfxiv.Audio;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Stats;
using HachidoriFfxiv.Ui;
using HachidoriFfxiv.WebView;

namespace HachidoriFfxiv;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/hachidori";
    private const string LegacyCommand = "/hoshi";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITextureReadbackProvider TextureReadback { get; private set; } = null!;
    [PluginService] internal static IGamepadState Gamepad { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

    private readonly WindowSystem windows = new("HachidoriFfxiv");
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly AudioPlayer audio;
    private readonly Fonts fonts;
    private readonly TextCapture capture;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly PopupManager popups;
    private readonly WebViewPopupController webView;
    private readonly Media.MediaMiner media;
    private readonly StatsTracker stats;
    private readonly AnkiCardFeed ankiFeed;
    private readonly GamepadNavigator gamepad;
    private readonly FreezeFrameOverlay freezeFrame;
    private readonly ReaderWindow reader;
    private readonly SearchWindow search;
    private readonly StatsWindow statsWindow;
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        DiagnosticLog.Write("plugin", $"Starting plugin on {PlatformInfo.Current.DisplayName} (wine={PlatformInfo.Current.IsWine}, steamDeck={PlatformInfo.Current.IsSteamDeck}, gamescope={PlatformInfo.Current.IsGamescope}, dcomp={PlatformInfo.Current.IsDirectCompositionAvailable}).");
        // Floating subtitles are shown in cutscenes, when Dalamud normally hides
        // plugin drawing. Keep dictionary popups usable there; retain user/Gpose hiding.
        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        config = LoadConfiguration();
        if (config.Migrate())
            config.Save();
        service = new HachidoriService(config,
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "offline"),
            PluginInterface.AssemblyLocation.DirectoryName);
        audio = new AudioPlayer(config);
        fonts = new Fonts(config);
        capture = new TextCapture(config);
        nativeHover = new NativeTextHover(config);
        publisher = new TextPublisher(config, capture);
        popups = new PopupManager(config, service, audio, fonts);
        media = new Media.MediaMiner(config, capture);
        stats = new StatsTracker(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "stats"),
            text => service.Link.Connected || (service.Offline?.Ready == true)
                ? service.SegmentAsync(text)
                : System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<WordSpan>>([]));
        capture.LineCaptured += OnLineCapturedForStats;
        ankiFeed = new AnkiCardFeed(media.Anki);
        popups.Media = media;
        popups.Stats = stats;
        webView = new WebViewPopupController(
            config,
            service,
            PluginInterface.GetPluginConfigDirectory(),
            () => media,
            () => stats,
            () => popups.NpcImages.Available,
            (expression, reading) => popups.QueueNpcCapture(expression, reading),
            _ => { if (configWindow is not null) configWindow.IsOpen = true; });
        popups.WebView = webView;
        nativeHover.ChatLinesChanged += OnChatLinesChanged;
        gamepad = new GamepadNavigator(config, popups, nativeHover, service);
        reader = new ReaderWindow(config, capture, popups, fonts);
        search = new SearchWindow(config, popups, fonts);
        freezeFrame = new FreezeFrameOverlay(config, nativeHover, service, popups, text => search.SetText(text));
        statsWindow = new StatsWindow(config, stats, ankiFeed, popups, fonts);
        configWindow = new ConfigWindow(config, service, fonts, nativeHover, publisher, popups.Themes, media, ExportLogs,
            openStats: statsWindow.Toggle,
            openSearchSample: sample => { if (sample is not null) search.SetText(sample); else search.Toggle(); },
            openReader: () => { reader.Pinned = true; config.ReaderEnabled = true; reader.IsOpen = true; config.Save(); },
            openPictures: popups.NpcImages.Open,
            webView: webView,
            triggerFreezeFrame: freezeFrame.Toggle);
        windows.AddWindow(reader);
        windows.AddWindow(search);
        windows.AddWindow(statsWindow);
        windows.AddWindow(configWindow);
        windows.AddWindow(popups.NpcImages);
        windows.AddWindow(freezeFrame);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Hachidori for FFXIV: /hachidori <text> · /hachidori freeze (tooltips) · /hachidori stats · /hachidori reader · /hachidori search · /hachidori pictures · /hachidori pad (controller) · /hachidori config · /hachidori logs",
        });
        CommandManager.AddHandler(LegacyCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /hachidori.", ShowInHelp = false,
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += search.Toggle;
    }

    private static Configuration LoadConfiguration()
    {
        var current = PluginInterface.GetPluginConfig() as Configuration;
        if (current is not null) return current;
        var legacy = System.IO.Path.Combine(PluginInterface.ConfigFile.DirectoryName!, "Hoshidori.json");
        if (System.IO.File.Exists(legacy) && !PluginInterface.ConfigFile.Exists)
        {
            try
            {
                // Ignore old serialized CLR type names after the namespace/assembly rename.
                var migrated = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(System.IO.File.ReadAllText(legacy),
                    new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None });
                if (migrated is not null)
                {
                    PluginInterface.SavePluginConfig(migrated);
                    return migrated;
                }
            }
            catch (System.Exception ex) { Log.Warning(ex, "Could not migrate the previous plugin settings."); }
        }
        return new Configuration();
    }

    private void OnLineCapturedForStats(GameLine line) =>
        stats.RecordLine(line.Source.ToString(), line.Speaker, line.Text, line.Time);

    private void OnChatLinesChanged(System.Collections.Generic.IReadOnlyList<string> lines)
    {
        if (!config.TokenizedHover) return;
        foreach (var line in lines)
            _ = service.SegmentAsync(line);
    }

    private void Draw()
    {
        using var sizedFonts = fonts.LockFrame();
        media.Draw();
        popups.NpcImages.PollOperations();
        freezeFrame.BeforeWindowsDraw();
        if (!freezeFrame.Active)
        {
            nativeHover.Draw(popups);
            gamepad.Draw();
        }
        reader.IsOpen = config.ReaderEnabled;
        windows.Draw();
        freezeFrame.AfterWindowsDraw();
        // After every scannable window (including freezeFrame), so this frame's hover decides the popups.
        popups.Draw();
    }

    private void OnCommand(string command, string args)
    {
        var text = args.Trim();
        switch (text)
        {
            case "":
            case "config":
            case "settings":
                configWindow.Toggle();
                return;
            case "reader":
                reader.Pinned = !reader.Pinned;
                config.ReaderEnabled = true;
                reader.IsOpen = true;
                return;
            case "search":
                search.Toggle();
                return;
            case "stats":
                statsWindow.Toggle();
                return;
            case "pictures":
                popups.NpcImages.Open();
                return;
            case "pad":
            case "controller":
                gamepad.Toggle();
                return;
            case "freeze":
            case "tooltip":
            case "tooltips":
                freezeFrame.Toggle();
                return;
            case "reconnect":
                service.Link.Reconnect();
                return;
            case "logs":
                try { ChatGui.Print($"Hachidori for FFXIV debug log: {ExportLogs()}"); }
                catch (System.Exception ex) { ChatGui.PrintError($"Hachidori for FFXIV log export failed: {ex.Message}"); }
                return;
            default:
                search.SetText(text);
                capture.AddManual(text);
                return;
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= search.Toggle;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(LegacyCommand);
        windows.RemoveAllWindows();
        capture.LineCaptured -= OnLineCapturedForStats;
        nativeHover.ChatLinesChanged -= OnChatLinesChanged;
        freezeFrame.Dispose();
        stats.Dispose();
        publisher.Dispose();
        capture.Dispose();
        nativeHover.Dispose();
        gamepad.Dispose();
        popups.Dispose();
        media.Dispose();
        audio.Dispose();
        fonts.Dispose();
        service.Dispose();
        DiagnosticLog.Write("plugin", "Plugin stopped.");
    }

    private string ExportLogs()
    {
        var platform = PlatformInfo.Current;
        var context = new System.Text.Json.Nodes.JsonObject
        {
            ["platform"] = platform.DisplayName,
            ["isWine"] = platform.IsWine,
            ["wineVersion"] = platform.WineVersion,
            ["hostOs"] = platform.HostOs.ToString(),
            ["isSteamDeck"] = platform.IsSteamDeck,
            ["isGamescope"] = platform.IsGamescope,
            ["supportsWebView2Overlay"] = platform.SupportsWebView2Overlay,
            ["supportsProcessAudioLoopback"] = platform.SupportsProcessAudioLoopback,
            ["audioRecorderMode"] = media.Recorder?.Mode.ToString() ?? "Off",
            ["connected"] = service.Link.Connected,
            ["hostVersion"] = service.Link.HostVersion,
            ["dictionaryCount"] = service.Link.DictionaryCount,
            ["backend"] = service.Backend.Name,
            ["offlineMode"] = config.OfflineMode,
            ["offlineEngine"] = service.Offline?.EngineVersion?.Short,
            ["offlineDictionaries"] = service.Offline?.Store.Manifest.Dictionaries.Count,
            ["offlineSyncedAt"] = service.Offline?.Store.Manifest.SyncedAt?.ToString("O"),
            ["offlineWarnings"] = service.Offline is { } offline ? offline.Warnings().Count : 0,
            ["webViewEnabled"] = config.UseWebViewPopup,
            ["webViewActive"] = webView.Active,
            ["webViewRuntime"] = webView.RuntimeAvailable,
            ["webViewVersion"] = webView.LoadedVersion,
            ["webViewBundleSource"] = webView.Bundles.ActiveSource,
            ["webViewError"] = webView.LastError,
            ["linkError"] = service.Link.Error is null ? null : "See relay events below",
            ["relayPort"] = config.RelayPort,
            ["audioApiPort"] = config.ApiPort,
            ["audioSources"] = "Resolved from sharing Chrome host on each uncached request",
            ["audioError"] = audio.LastError is null ? null : "See audio events below",
            ["volume"] = config.AudioVolume,
            ["tokenizedHover"] = config.TokenizedHover,
            ["scanModifier"] = config.ScanModifier.ToString(),
            ["nativeHover"] = config.NativeTextHover,
            ["nativeRegionCount"] = nativeHover.RegionCount,
            ["nativeChatHover"] = config.NativeChatHover,
            ["captureChat"] = config.CaptureChat,
            ["lastHoveredAddon"] = nativeHover.LastHoveredAddon,
            ["publishEnabled"] = config.PublishEnabled,
            ["publishPort"] = config.PublishPort,
            ["publishClients"] = publisher.ClientCount,
            ["talkVisible"] = capture.TalkVisible,
            ["floatingSubtitleVisible"] = nativeHover.SubtitleVisible,
            ["battleTalkVisible"] = capture.BattleTalkVisible,
            ["cutsceneDrawingEnabled"] = PluginInterface.UiBuilder.DisableCutsceneUiHide,
            ["lastCapturedSource"] = capture.Current?.Source.ToString(),
            ["lastCapturedCharacterCount"] = capture.Current?.Text.Length ?? 0,
        };
        return DiagnosticLog.Export(System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "logs"), context);
    }
}
