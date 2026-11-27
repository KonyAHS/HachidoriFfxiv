using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dalamud.Plugin;

namespace HachidoriFfxiv;

/// <summary>
/// What exactly is running: the plugin commit (and whether the tree was dirty), the game patch,
/// Dalamud, and the offline engine beside the plugin. Stamped into every debug export and at the
/// top of each persistent log session, because the assembly version alone repeats across builds.
/// </summary>
internal static class BuildStamp
{
    public static JsonObject Collect(IDalamudPluginInterface pluginInterface)
    {
        var plugin = typeof(BuildStamp).Assembly;
        var dalamud = typeof(IDalamudPluginInterface).Assembly;
        var assemblyDir = pluginInterface.AssemblyLocation.DirectoryName;
        return new JsonObject
        {
            ["plugin"] = Informational(plugin),
            ["pluginAssembly"] = plugin.GetName().Version?.ToString(),
            ["pluginDllSha256"] = assemblyDir is null ? null : Sha256Prefix(Path.Combine(assemblyDir, "HachidoriFfxiv.dll")),
            ["game"] = GameVersion(),
            ["dalamud"] = Informational(dalamud),
            ["dalamudAssembly"] = dalamud.GetName().Version?.ToString(),
            ["nativeEngine"] = assemblyDir is null ? null : NativeEngine(assemblyDir),
            ["runtime"] = Environment.Version.ToString(),
            ["os"] = Environment.OSVersion.VersionString,
            ["sessionStartedUtc"] = DateTime.UtcNow.ToString("O"),
        };
    }

    private static string? Informational(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static string? GameVersion()
    {
        try
        {
            var gameDir = Path.GetDirectoryName(Environment.ProcessPath);
            var ver = gameDir is null ? null : Path.Combine(gameDir, "ffxivgame.ver");
            return ver is not null && File.Exists(ver) ? File.ReadAllText(ver).Trim() : null;
        }
        catch (Exception ex) { return $"unreadable: {ex.GetType().Name}"; }
    }

    private static JsonObject? NativeEngine(string assemblyDir)
    {
        var dll = Path.Combine(assemblyDir, "hdw_native.dll");
        if (!File.Exists(dll)) return null;
        var result = new JsonObject { ["dllSha256"] = Sha256Prefix(dll) };
        try
        {
            var manifest = Path.Combine(assemblyDir, "hdw_native.json");
            if (File.Exists(manifest) && JsonNode.Parse(File.ReadAllText(manifest)) is JsonObject m)
            {
                result["hachidori"] = m["hachidori"]?.DeepClone();
                result["hachidoriCommit"] = m["hachidoriCommit"]?.DeepClone();
                result["hoshidictsCommit"] = m["hoshidictsCommit"]?.DeepClone();
                result["dictionaryFormat"] = m["dictionaryFormat"]?.DeepClone();
            }
        }
        catch (Exception ex) { result["manifestError"] = ex.GetType().Name; }
        return result;
    }

    private static string? Sha256Prefix(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream), 0, 8).ToLowerInvariant();
        }
        catch (Exception ex) { return $"unreadable: {ex.GetType().Name}"; }
    }
}
