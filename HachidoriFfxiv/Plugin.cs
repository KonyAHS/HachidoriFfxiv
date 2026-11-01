using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HachidoriFfxiv.Audio;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Ui;

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

    private readonly WindowSystem windows = new("HachidoriFfxiv");
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly AudioPlayer audio;
    private readonly Fonts fonts;
    private readonly TextCapture capture;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly PopupManager popups;
    private readonly ReaderWindow reader;
    private readonly SearchWindow search;
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        DiagnosticLog.Write("plugin", "Starting plugin.");
        // Floating subtitles are shown in cutscenes, when Dalamud normally hides
        // plugin drawing. Keep dictionary popups usable there; retain user/Gpose hiding.
        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        config = LoadConfiguration();
        service = new HachidoriService(config);
        audio = new AudioPlayer(config);
        fonts = new Fonts(config);
        capture = new TextCapture(config);
        nativeHover = new NativeTextHover(config);
        publisher = new TextPublisher(config, capture);
        popups = new PopupManager(config, service, audio, fonts);
        reader = new ReaderWindow(config, capture, popups, fonts);
        search = new SearchWindow(config, popups, fonts);
        configWindow = new ConfigWindow(config, service, fonts, nativeHover, publisher, ExportLogs);
        windows.AddWindow(reader);
        windows.AddWindow(search);
        windows.AddWindow(configWindow);
        windows.AddWindow(popups.NpcImages);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Hachidori for FFXIV: /hachidori <text> · /hachidori reader · /hachidori search · /hachidori config · /hachidori logs",
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

    private void Draw()
    {
        using var sizedFonts = fonts.LockFrame();
        popups.NpcImages.PollOperations();
        nativeHover.Draw(popups);
        reader.IsOpen = config.ReaderEnabled;
        windows.Draw();
        // After every scannable window, so this frame's hover decides the popups.
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
        publisher.Dispose();
        capture.Dispose();
        nativeHover.Dispose();
        popups.Dispose();
        audio.Dispose();
        fonts.Dispose();
        service.Dispose();
        DiagnosticLog.Write("plugin", "Plugin stopped.");
    }

    private string ExportLogs()
    {
        var context = new System.Text.Json.Nodes.JsonObject
        {
            ["connected"] = service.Link.Connected,
            ["hostVersion"] = service.Link.HostVersion,
            ["dictionaryCount"] = service.Link.DictionaryCount,
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
