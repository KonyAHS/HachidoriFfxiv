using System;
using Dalamud.Game;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HachidoriFfxiv;

/// <summary>
/// Leaf holder for Dalamud IoC services, populated once at plugin startup via
/// <see cref="IDalamudPluginInterface.Create{T}"/> so subsystems never back-reference <see cref="Plugin"/>.
/// </summary>
public sealed class DalamudServices
{
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
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    internal static void Initialize(IDalamudPluginInterface pluginInterface)
    {
        if (pluginInterface.Create<DalamudServices>() is null)
            throw new InvalidOperationException("Failed to initialize Dalamud plugin services.");
        PluginInterface ??= pluginInterface;
    }

    internal static void NotifyAnkiCardCreated(bool enabled, string expression, string reading, bool updated = false)
    {
        if (!enabled || NotificationManager is null)
            return;
        var word = string.IsNullOrWhiteSpace(expression)
            ? "Card saved"
            : !string.IsNullOrWhiteSpace(reading) && !string.Equals(reading, expression, StringComparison.Ordinal)
                ? $"{expression} ({reading})"
                : expression;
        var title = updated ? "Anki card updated" : "Anki card created";
        try
        {
            NotificationManager.AddNotification(new Notification
            {
                Title = title,
                Content = word,
                MinimizedText = $"{title}: {word}",
                Type = NotificationType.Success,
                InitialDuration = TimeSpan.FromSeconds(3),
                RespectUiHidden = false,
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("anki", $"Notification skipped: {ex.Message}");
        }
    }
}
