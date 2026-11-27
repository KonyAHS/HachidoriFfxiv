using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv;

internal enum DiagLevel { Debug, Info, Warn, Error }

/// <summary>
/// The plugin's single diagnostic channel. Events go to an in-memory ring (exported by
/// <c>/hachidori logs</c>), to a rotating JSONL file once <see cref="StartSession"/> names a
/// directory (so a crash or reload keeps the evidence), and Warn/Error are forwarded to
/// dalamud.log through <see cref="Forward"/>. Startup milestones live in their own bucket
/// so a long session cannot evict them. Dalamud-free so the regression checks compile it.
/// </summary>
internal static partial class DiagnosticLog
{
    private const int RingCapacity = 500;
    private const int MilestoneCapacity = 100;
    private const long FileLimitBytes = 1024 * 1024;
    private const int FileGenerations = 3;
    private static readonly TimeSpan ForwardCooldown = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static readonly Queue<Event> Events = new();
    private static readonly Queue<Event> Milestones = new();
    private static readonly Dictionary<string, CaughtStats> Caught = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> LastForwarded = new(StringComparer.Ordinal);
    private static readonly object FileGate = new();
    private static StreamWriter? file;
    private static string? filePath;
    private static long fileBytes;
    private static int nextOp;
    private static JsonObject build = new();
    // Logs are read as files, never embedded in HTML: keep '+' in versions and Japanese text readable.
    private static readonly JsonSerializerOptions LineJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions ExportJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    [GeneratedRegex(@"(?:https?|wss?)://[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex RedactUrlRegex();

    // Failure wording that marks an unlevelled Write as a warning.
    [GeneratedRegex(@"\b(fail(ed|s|ure)?|errors?|exception|unavailable|could not|cannot|timed out|rejected|not detected|skipped a frame)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningWordingRegex();

    /// <summary>Receives Warn and Error events for dalamud.log (rate-limited per message).</summary>
    public static Action<DiagLevel, string>? Forward { get; set; }

    /// <summary>The rotating JSONL file this session appends to, when persistence is on.</summary>
    public static string? SessionFile => filePath;

    private readonly record struct Event(DateTime Utc, DiagLevel Level, string Area, string Message, string? Op, double? Ms);

    private sealed class CaughtStats
    {
        public required string Area { get; init; }
        public required string Type { get; init; }
        public required string Frame { get; init; }
        public required string Context { get; init; }
        public int Count;
        public string LastMessage = "";
    }

    /// <summary>Info, or Warn when the wording reports a failure.</summary>
    public static void Write(string area, string message) =>
        Write(area, message, WarningWordingRegex().IsMatch(message) ? DiagLevel.Warn : DiagLevel.Info);

    public static void Write(string area, string message, DiagLevel level, string? op = null, double? ms = null)
    {
        // Never store request bodies, media, dialogue, source URLs, or credentials.
        var safe = RedactUrlRegex().Replace(message, "<url>");
        if (safe.Length > 2000) safe = safe[..2000];
        var e = new Event(DateTime.UtcNow, level, area, safe, op, ms);
        lock (Gate)
        {
            Events.Enqueue(e);
            while (Events.Count > RingCapacity) Events.Dequeue();
        }
        Persist(e);
        if (level >= DiagLevel.Warn) ForwardOnce(e);
    }

    public static void Warn(string area, string message, string? op = null) => Write(area, message, DiagLevel.Warn, op);

    /// <summary>A one-off startup or capability fact (hooks, handshake, engine load) that is never evicted.</summary>
    public static void Milestone(string area, string message, DiagLevel level = DiagLevel.Info)
    {
        Write(area, message, level);
        lock (Gate)
        {
            Milestones.Enqueue(new Event(DateTime.UtcNow, level, area, RedactUrlRegex().Replace(message, "<url>"), null, null));
            while (Milestones.Count > MilestoneCapacity) Milestones.Dequeue();
        }
    }

    /// <summary>
    /// Records a caught exception by type and stack fingerprint. Repeats of the same fingerprint are
    /// counted and logged again only at 10, 100, 1000… so per-frame failures stay visible but cheap.
    /// </summary>
    public static void Fault(string area, Exception ex, string context, DiagLevel level = DiagLevel.Warn)
    {
        var (hash, frame) = Fingerprint(ex);
        var key = $"{area}|{hash}";
        int count;
        lock (Gate)
        {
            if (!Caught.TryGetValue(key, out var stats))
                Caught[key] = stats = new CaughtStats { Area = area, Type = ex.GetType().FullName ?? ex.GetType().Name, Frame = frame, Context = context };
            count = ++stats.Count;
            stats.LastMessage = ex.Message;
        }
        if (count != 1 && !IsPowerOfTen(count)) return;
        var repeat = count > 1 ? $" (seen {count}x)" : "";
        Write(area, $"{context}: {ex.GetType().Name} [{hash}] at {frame}: {ex.Message}{repeat}", level);
    }

    /// <summary>
    /// Logs faulted fire-and-forget tasks that nobody awaited. The scheduler event is process-wide,
    /// so only exceptions thrown through this plugin's code are recorded; others are left alone.
    /// </summary>
    public static void CaptureUnobservedTasks(bool enabled)
    {
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
        if (enabled) TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        foreach (var inner in args.Exception.Flatten().InnerExceptions)
        {
            if (inner.ToString().Contains("HachidoriFfxiv.", StringComparison.Ordinal))
                Fault("task", inner, "Unobserved task exception", DiagLevel.Error);
        }
    }

    /// <summary>A short id that ties the events of one lookup, mine or request together.</summary>
    public static string NewOp(string prefix) => $"{prefix}-{Interlocked.Increment(ref nextOp):x}";

    public static long Now() => Stopwatch.GetTimestamp();

    public static double MsSince(long start) => Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1);

    /// <summary>
    /// Stamps the build into every export and starts appending events to a rotating JSONL file
    /// in <paramref name="directory"/>. The first line of each session is the build stamp.
    /// </summary>
    public static void StartSession(string directory, JsonObject buildStamp)
    {
        lock (Gate) build = (JsonObject)buildStamp.DeepClone();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "hachidori-diag.jsonl");
            lock (FileGate)
            {
                file?.Dispose();
                filePath = path;
                OpenFile();
                WriteLine(new JsonObject { ["t"] = DateTime.UtcNow.ToString("O"), ["session"] = buildStamp.DeepClone() }.ToJsonString(LineJson));
            }
        }
        catch (Exception ex)
        {
            lock (FileGate) { file = null; filePath = null; }
            Write("diag", $"Persistent log unavailable: {ex.Message}", DiagLevel.Warn);
        }
        lock (Gate)
            foreach (var e in Events) Persist(e);
    }

    public static void StopSession()
    {
        lock (FileGate)
        {
            file?.Dispose();
            file = null;
            filePath = null;
        }
    }

    public static string Export(string directory, JsonObject context)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-debug-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        lock (Gate)
        {
            var document = new JsonObject
            {
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["pluginVersion"] = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(),
                ["build"] = build.DeepClone(),
                ["sessionLog"] = filePath,
                ["context"] = context.DeepClone(),
                ["milestones"] = new JsonArray(Milestones.Select(e => (JsonNode?)ToJson(e)).ToArray()),
                ["caughtExceptions"] = new JsonArray(Caught.Values.OrderByDescending(c => c.Count).Select(c => (JsonNode?)new JsonObject
                {
                    ["area"] = c.Area,
                    ["type"] = c.Type,
                    ["frame"] = c.Frame,
                    ["context"] = c.Context,
                    ["count"] = c.Count,
                    ["lastMessage"] = RedactUrlRegex().Replace(c.LastMessage, "<url>"),
                }).ToArray()),
                ["events"] = new JsonArray(Events.Select(e => (JsonNode?)ToJson(e)).ToArray()),
            };
            File.WriteAllText(path, document.ToJsonString(ExportJson));
        }
        return path;
    }

    /// <summary>Exception type plus the top plugin frames, hashed; stable across builds of the same code.</summary>
    internal static (string Hash, string Frame) Fingerprint(Exception ex)
    {
        var frames = new StackTrace(ex, false).GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m is not null)
            .Select(m => $"{m!.DeclaringType?.Name}.{m.Name}")
            .ToArray();
        var own = frames.FirstOrDefault(f => !f.StartsWith("<", StringComparison.Ordinal)) ?? frames.FirstOrDefault() ?? "(no stack)";
        var text = ex.GetType().FullName + "|" + string.Join("|", frames.Take(6));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 4).ToLowerInvariant();
        return (hash, own);
    }

    private static bool IsPowerOfTen(int n)
    {
        while (n >= 10 && n % 10 == 0) n /= 10;
        return n == 1;
    }

    private static JsonObject ToJson(Event e)
    {
        var o = new JsonObject
        {
            ["t"] = e.Utc.ToString("O"),
            ["lvl"] = e.Level.ToString().ToLowerInvariant(),
            ["area"] = e.Area,
        };
        if (e.Op is not null) o["op"] = e.Op;
        if (e.Ms is { } ms) o["ms"] = ms;
        o["msg"] = e.Message;
        return o;
    }

    private static void ForwardOnce(Event e)
    {
        var sink = Forward;
        if (sink is null) return;
        var key = e.Area + "|" + (e.Message.Length > 80 ? e.Message[..80] : e.Message);
        lock (Gate)
        {
            if (LastForwarded.TryGetValue(key, out var last) && e.Utc - last < ForwardCooldown) return;
            if (LastForwarded.Count > 256) LastForwarded.Clear();
            LastForwarded[key] = e.Utc;
        }
        try { sink(e.Level, $"[{e.Area}] {e.Message}"); }
        catch { /* the host log is best-effort; the ring and file already hold the event */ }
    }

    private static void Persist(Event e)
    {
        if (file is null) return;
        var line = ToJson(e).ToJsonString(LineJson);
        lock (FileGate)
        {
            if (file is null) return;
            try
            {
                if (fileBytes + line.Length > FileLimitBytes) Rotate();
                WriteLine(line);
            }
            catch
            {
                // Disk full or the file was taken away: stop persisting rather than throw into callers.
                file?.Dispose();
                file = null;
            }
        }
    }

    private static void WriteLine(string line)
    {
        file!.WriteLine(line);
        fileBytes += Encoding.UTF8.GetByteCount(line) + 2;
    }

    private static void OpenFile()
    {
        var stream = new FileStream(filePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fileBytes = stream.Length;
        file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void Rotate()
    {
        file?.Dispose();
        var stem = Path.Combine(Path.GetDirectoryName(filePath!)!, Path.GetFileNameWithoutExtension(filePath!));
        for (var i = FileGenerations - 1; i >= 1; i--)
        {
            var from = i == 1 ? filePath! : $"{stem}.{i - 1}.jsonl";
            var to = $"{stem}.{i}.jsonl";
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        OpenFile();
    }
}
