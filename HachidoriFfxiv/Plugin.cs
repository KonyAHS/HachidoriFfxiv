using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using HachidoriFfxiv.Anki;
using HachidoriFfxiv.Audio;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Offline;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Stats;
using HachidoriFfxiv.Ui;
using HachidoriFfxiv.WebView;

namespace HachidoriFfxiv;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/hachidori";
    private const string LegacyCommand = "/hoshi";

    private readonly WindowSystem windows = new("HachidoriFfxiv");
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly OfflineMode? offline;
    private readonly AudioPlayer audio;
    private readonly Fonts fonts;
    private readonly TextCapture capture;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly PopupManager popups;
    private readonly WebViewPopupController webView;
    private readonly Media.MediaMiner media;
    private readonly StatsTracker stats;
    private readonly FfxivMsqResolver msqResolver;
    private readonly AnkiCardFeed ankiFeed;
    private readonly DialogueBacklog backlog;
    private readonly GamepadNavigator gamepad;
    private readonly FreezeFrameOverlay freezeFrame;
    private readonly ReaderWindow reader;
    private readonly SearchWindow search;
    private readonly StatsWindow statsWindow;
    private readonly ConfigWindow configWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        DiagnosticLog.Forward = ForwardToDalamudLog;
        DiagnosticLog.StartSession(
            System.IO.Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "logs"),
            BuildStamp.Collect(pluginInterface));
        DiagnosticLog.CaptureUnobservedTasks(true);
        DiagnosticLog.Milestone("plugin", $"Starting plugin on {PlatformInfo.Current.DisplayName} (wine={PlatformInfo.Current.IsWine}, steamDeck={PlatformInfo.Current.IsSteamDeck}, gamescope={PlatformInfo.Current.IsGamescope}, dcomp={PlatformInfo.Current.IsDirectCompositionAvailable}).");
        // Floating subtitles are shown in cutscenes, when Dalamud normally hides
        // plugin drawing. Keep dictionary popups usable there; retain user/Gpose hiding.
        DalamudServices.PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        config = LoadConfiguration();
        if (config.Migrate())
            config.Save();
        service = new HachidoriService(config);
        var assemblyDir = DalamudServices.PluginInterface.AssemblyLocation.DirectoryName;
        LocalBackend? localBackend = null;
        if (assemblyDir is not null)
        {
            offline = new OfflineMode(config, service.Link,
                System.IO.Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "offline"),
                assemblyDir);
            localBackend = new LocalBackend(offline, service.Http, () => service.Options);
        }
        var offlineAnki = new OfflineAnkiMiner(() => service.Snapshot, () => offline is { Ready: true } o ? o.Loaded() : null, service.Http);
        service.AttachOffline(localBackend, offlineAnki);
        audio = new AudioPlayer(config);
        fonts = new Fonts(config);
        capture = new TextCapture(config);
        nativeHover = new NativeTextHover(config);
        publisher = new TextPublisher(config, capture);
        popups = new PopupManager(config, service, audio, fonts);
        media = new Media.MediaMiner(config, capture, () => !string.IsNullOrWhiteSpace(config.AnkiConnectApiKey) ? config.AnkiConnectApiKey.Trim() : service.AnkiApiKey);
        audio.PlaybackTracker = media.PluginAudio;
        stats = new StatsTracker(
            System.IO.Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "stats"),
            text => service.Link.Connected || (offline?.Ready == true)
                ? service.SegmentAsync(text)
                : System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<WordSpan>>([]),
            options: new StatsTrackerOptions
            {
                GameTitle = () => config.DocumentTitle,
                DayStartHour = () => config.StatsDayStartHour,
                UseRubiAdaptiveTime = () => config.StatsUseRubiAdaptiveTime,
                WriteHistoryCsv = () => config.StatsWriteHistoryCsv,
                WriteTidepoolSqlite = () => config.StatsWriteTidepoolSqlite,
            });
        capture.LineCaptured += OnLineCapturedForStats;
        msqResolver = new FfxivMsqResolver(DalamudServices.DataManager);
        ankiFeed = new AnkiCardFeed(media.Anki);
        popups.Media = media;
        popups.Stats = stats;
        webView = new WebViewPopupController(
            config,
            service,
            offline,
            DalamudServices.PluginInterface.GetPluginConfigDirectory(),
            () => media,
            () => stats,
            () => popups.NpcImages.Available,
            (expression, reading) => popups.QueueNpcCapture(expression, reading),
            _ => { if (configWindow is not null) configWindow.IsOpen = true; });
        popups.WebView = webView;
        nativeHover.ChatLinesChanged += OnChatLinesChanged;
        backlog = new DialogueBacklog(config, capture, media, popups, service, audio, fonts);
        gamepad = new GamepadNavigator(config, popups, nativeHover, service, capture, backlog);
        reader = new ReaderWindow(config, capture, popups, fonts);
        search = new SearchWindow(config, popups, fonts);
        freezeFrame = new FreezeFrameOverlay(config, nativeHover, service, popups, text => search.SetText(text));
        statsWindow = new StatsWindow(config, stats, ankiFeed, popups, fonts, msqResolver);
        configWindow = new ConfigWindow(config, service, offline, fonts, nativeHover, publisher, popups.Themes, media, ExportLogs,
            openStats: statsWindow.Toggle,
            openSearchSample: sample => { if (sample is not null) search.SetText(sample); else search.Toggle(); },
            openReader: () => { reader.Pinned = true; config.ReaderEnabled = true; reader.IsOpen = true; config.Save(); },
            openPictures: popups.NpcImages.Open,
            webView: webView,
            triggerFreezeFrame: freezeFrame.Toggle,
            gamepad: gamepad);
        windows.AddWindow(reader);
        windows.AddWindow(search);
        windows.AddWindow(statsWindow);
        windows.AddWindow(configWindow);
        windows.AddWindow(popups.NpcImages);
        windows.AddWindow(freezeFrame);

        DalamudServices.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Hachidori for FFXIV settings. /hachidori help lists more commands.",
        });
        DalamudServices.CommandManager.AddHandler(LegacyCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /hachidori.", ShowInHelp = false,
        });

        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
    }

    private static Configuration LoadConfiguration()
    {
        var current = DalamudServices.PluginInterface.GetPluginConfig() as Configuration;
        if (current is not null) return current;
        var legacy = System.IO.Path.Combine(DalamudServices.PluginInterface.ConfigFile.DirectoryName!, "Hoshidori.json");
        if (System.IO.File.Exists(legacy) && !DalamudServices.PluginInterface.ConfigFile.Exists)
        {
            try
            {
                // Ignore old serialized CLR type names after the namespace/assembly rename.
                var migrated = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(System.IO.File.ReadAllText(legacy),
                    new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None });
                if (migrated is not null)
                {
                    DalamudServices.PluginInterface.SavePluginConfig(migrated);
                    return migrated;
                }
            }
            catch (System.Exception ex) { DiagnosticLog.Fault("plugin", ex, "Could not migrate the previous plugin settings"); }
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
            case "backlog":
            case "history":
                gamepad.ToggleBacklog();
                return;
            case "freeze":
            case "tooltip":
            case "tooltips":
                freezeFrame.Toggle();
                return;
            case "reconnect":
                service.Link.Reconnect();
                return;
            case "diag":
            case "health":
                PrintDiagnostics();
                return;
            case "help":
                DalamudServices.ChatGui.Print("Hachidori for FFXIV: /hachidori (settings) · /hachidori <text> · /hachidori freeze (tooltips) · /hachidori stats · /hachidori reader · /hachidori search · /hachidori pictures · /hachidori pad (controller) · /hachidori backlog (history) · /hachidori diag · /hachidori logs");
                return;
            case "logs":
                try
                {
                    DalamudServices.ChatGui.Print($"Hachidori for FFXIV debug log: {ExportLogs()}");
                    if (DiagnosticLog.SessionFile is { } sessionLog)
                        DalamudServices.ChatGui.Print($"Hachidori for FFXIV session log (survives crashes): {sessionLog}");
                }
                catch (System.Exception ex) { DalamudServices.ChatGui.PrintError($"Hachidori for FFXIV log export failed: {ex.Message}"); }
                return;
            default:
                search.SetText(text);
                capture.AddManual(text);
                return;
        }
    }

    public void Dispose()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        DalamudServices.CommandManager.RemoveHandler(Command);
        DalamudServices.CommandManager.RemoveHandler(LegacyCommand);
        windows.RemoveAllWindows();
        capture.LineCaptured -= OnLineCapturedForStats;
        nativeHover.ChatLinesChanged -= OnChatLinesChanged;
        freezeFrame.Dispose();
        stats.Dispose();
        publisher.Dispose();
        capture.Dispose();
        nativeHover.Dispose();
        gamepad.Dispose();
        backlog.Dispose();
        popups.Dispose();
        media.Dispose();
        audio.Dispose();
        fonts.Dispose();
        service.Dispose();
        offline?.Dispose();
        DiagnosticLog.Milestone("plugin", "Plugin stopped.");
        DiagnosticLog.CaptureUnobservedTasks(false);
        DiagnosticLog.Forward = null;
        DiagnosticLog.StopSession();
    }

    private void PrintDiagnostics()
    {
        var ui = GameUiInterop.InspectHealth();
        var talkSummary = ui.TalkVisible
            ? $"Talk visible (body={ui.TalkBodyResolved}, speaker={ui.TalkSpeakerResolved})"
            : "Talk hidden";
        var chatSummary = ui.ChatLogVisible
            ? $"ChatLog visible (tab={ui.SelectedChatTab}, panels={ui.ChatPanelCount})"
            : $"ChatLog hidden (panels={ui.ChatPanelCount})";
        var relaySummary = service.Link.Connected
            ? $"connected v{service.Link.HostVersion} (proto v{service.Link.PeerProtocolVersion}, dicts={service.Link.DictionaryCount}, caps={service.Link.HostCapabilities.Length})"
            : $"disconnected ({service.Link.Error ?? "not linked"}; backend={service.Backend.Name})";
        var webViewSummary = webView.Active
            ? $"active v{webView.LoadedVersion} (smokeTest={webView.SmokeTestPassed}, fallbackSelector={webView.UsedFallbackSelector})"
            : $"inactive ({webView.LastError ?? webView.StatusText})";
        var msqIndex = msqResolver.Index;
        var msqSummary = msqIndex is not null
            ? $"{msqIndex.Quests.Count} quests across {ExpansionEstimates.All.Count} expansions"
            : msqResolver.LoadError is { } err
                ? $"error: {err}"
                : msqResolver.IsLoading ? "loading" : "not loaded";

        var lines = new[]
        {
            $"[Hachidori Diag] UI: {talkSummary}; {chatSummary}; hoverRegions={nativeHover.RegionCount}{(nativeHover.LastError is { } he ? $"; err={he}" : "")}",
            $"[Hachidori Diag] Voice: {media.Voices.DetectionModeSummary} (hooks={media.Voices.LoadHookActive}/{media.Voices.PlayHookActive}, pool={media.Voices.PoolAvailable})",
            $"[Hachidori Diag] WebView2: {webViewSummary}",
            $"[Hachidori Diag] Relay: {relaySummary}",
            $"[Hachidori Diag] MSQ: {msqSummary}",
        };
        foreach (var line in lines)
        {
            DalamudServices.ChatGui.Print(line);
            DiagnosticLog.Write("diag", line);
        }
    }

    private static void ForwardToDalamudLog(DiagLevel level, string message)
    {
        if (level >= DiagLevel.Error) DalamudServices.Log.Error("HachidoriFfxiv {Message}", message);
        else DalamudServices.Log.Warning("HachidoriFfxiv {Message}", message);
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
            ["voiceHooksActive"] = media.Voices.HooksActive,
            ["voicePoolAvailable"] = media.Voices.PoolAvailable,
            ["voiceDetectionMode"] = media.Voices.DetectionModeSummary,
            ["lastVoicePath"] = media.Voices.LastVoicePath,
            ["ttsInjectOst"] = config.TtsInjectOst,
            ["ttsOstVolume"] = config.TtsOstVolume,
            ["bgmTrack"] = media.Bgm.LastSnapshot?.FilePath,
            ["bgmElapsedSeconds"] = media.Bgm.LastSnapshot is { } bgmSnap ? System.Math.Round(bgmSnap.ElapsedSeconds, 1) : null,
            ["bgmEngineClock"] = media.Bgm.LastSnapshot?.FromEngineClock,
            ["bgmMuted"] = media.Bgm.BgmMutedInGame,
            ["connected"] = service.Link.Connected,
            ["peerProtocolVersion"] = service.Link.PeerProtocolVersion,
            ["hostVersion"] = service.Link.HostVersion,
            ["dictionaryCount"] = service.Link.DictionaryCount,
            ["backend"] = service.Backend.Name,
            ["offlineMode"] = config.OfflineMode,
            ["offlineEngine"] = offline?.EngineVersion?.Short,
            ["offlineDictionaries"] = offline?.Store.Manifest.Dictionaries.Count,
            ["offlineSyncedAt"] = offline?.Store.Manifest.SyncedAt?.ToString("O"),
            ["offlineWarnings"] = offline is { } off ? off.Warnings().Count : 0,
            ["webViewEnabled"] = config.UseWebViewPopup,
            ["webViewActive"] = webView.Active,
            ["webViewRuntime"] = webView.RuntimeAvailable,
            ["webViewVersion"] = webView.LoadedVersion,
            ["webViewBundleSource"] = webView.Bundles.ActiveSource,
            ["webViewSmokeTestPassed"] = webView.SmokeTestPassed,
            ["webViewUsedFallbackSelector"] = webView.UsedFallbackSelector,
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
            ["cutsceneDrawingEnabled"] = DalamudServices.PluginInterface.UiBuilder.DisableCutsceneUiHide,
            ["lastCapturedSource"] = capture.Current?.Source.ToString(),
            ["lastCapturedCharacterCount"] = capture.Current?.Text.Length ?? 0,
        };
        return DiagnosticLog.Export(System.IO.Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "logs"), context);
    }
}
