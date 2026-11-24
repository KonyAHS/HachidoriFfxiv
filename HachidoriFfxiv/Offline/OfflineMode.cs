using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// Optional offline mode: a local copy of the user's Hachidori dictionaries and
/// settings, answered by the native engine while the live host is unreachable.
/// Live Hachidori is always preferred when it is linked; this keeps the local
/// copy in step with it on every connection.
/// </summary>
public sealed class OfflineMode : IDisposable
{
    private static readonly TimeSpan SnapshotSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncDelay = TimeSpan.FromSeconds(3);

    private readonly Configuration config;
    private readonly HachidoriLink link;
    private readonly string libraryDirectory;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    // Loading the DLL and loading dictionaries are guarded apart: the draw thread
    // asks whether the engine exists every frame and must not wait for a load.
    private readonly object engineGate = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private NativeEngine? native;
    private volatile LocalEngine? engine;
    private volatile string? engineError;
    private bool loaded;
    private string? loadedState;
    private CancellationTokenSource? pendingSave;
    private CancellationTokenSource? pendingSync;
    private Task? syncTask;
    private string? syncedRevisions;

    public OfflineMode(Configuration config, HachidoriLink link, string root, string libraryDirectory)
    {
        this.config = config;
        this.link = link;
        this.libraryDirectory = libraryDirectory;
        Store = new OfflineStore(root);
        Sync = new OfflineSync(Store, http, () => new UriBuilder("http", config.RelayHost, config.ApiPort, "/").Uri, NewestFormat);
        try { Store.CleanUnreferenced(); }
        catch (Exception ex) { DiagnosticLog.Write("offline", $"Cleanup failed: {ex.Message}"); }
        link.ConnectionChanged += OnConnectionChanged;
        link.SnapshotChanged += OnSnapshotChanged;
    }

    public OfflineStore Store { get; }
    public OfflineSync Sync { get; }

    /// <summary>Raised after the local dictionaries or their load changed, so cached lookups are dropped.</summary>
    public event Action? Changed;

    public bool Enabled => config.OfflineMode;

    /// <summary>Set up, with dictionaries on disk and an engine that loads.</summary>
    public bool Ready => Enabled && Store.HasDictionaries && Engine() is not null;

    public string? EngineError => engineError;

    public string? LastSyncError { get; private set; }

    public SyncResult? LastSync { get; private set; }

    public NativeVersion? EngineVersion => Engine()?.Version;

    public IReadOnlyList<(LocalDictionary Dictionary, string Error)> LoadFailures => engine?.Failures ?? [];

    private int NewestFormat => Engine()?.Version.DictionaryFormat ?? 0;

    // ------------------------------------------------------------- the engine

    private LocalEngine? Engine()
    {
        if (engine is { } ready) return ready;
        if (engineError is not null || lifetime.IsCancellationRequested) return null;
        lock (engineGate)
        {
            if (engine is not null || engineError is not null) return engine;
            try
            {
                native = NativeEngine.LoadFrom(libraryDirectory);
                engine = new LocalEngine(native, () => KanjiFurigana.Shared);
                DiagnosticLog.Write("offline", $"Loaded {native.Version.Short}.");
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                           or InvalidOperationException or OfflineEngineException or IOException)
            {
                engineError = $"The offline engine could not load: {ex.Message}";
                DiagnosticLog.Write("offline", engineError);
            }
            return engine;
        }
    }

    /// <summary>The engine with the current dictionaries loaded, loading them first when needed.</summary>
    public LocalEngine Loaded()
    {
        var local = Engine() ?? throw new OfflineEngineException(EngineError ?? "The offline engine is unavailable.");
        lock (gate)
        {
            var snapshot = Store.Snapshot ?? link.Snapshot;
            var plan = OfflinePlan.For(Store, snapshot);
            var state = string.Join("\n", plan.Select(d => $"{d.Path}|{d.Enabled}"));
            if (loaded && state == loadedState) return local;
            var started = DateTime.UtcNow;
            local.Load(plan);
            loaded = true;
            loadedState = state;
            DiagnosticLog.Write("offline", $"Loaded {local.Loaded.Count} dictionaries ({local.DictionaryCount} kinds) in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms; {local.Failures.Count} failed.");
            foreach (var (dictionary, error) in local.Failures)
                DalamudServices.Log.Warning($"Offline dictionary {dictionary.Title} did not load: {error}");
        }
        Changed?.Invoke();
        return local;
    }

    /// <summary>Unloads the dictionaries; the next offline lookup loads them again.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            if (loaded) engine?.Unload();
            loaded = false;
            loadedState = null;
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ setup / sync

    /// <summary>Turns offline mode on and downloads everything. Needs Hachidori linked.</summary>
    public Task SetUpAsync()
    {
        config.OfflineMode = true;
        config.Save();
        if (link.Snapshot.Count > 0) Store.SaveSnapshot(link.Snapshot);
        return SyncNowAsync();
    }

    public Task SyncNowAsync()
    {
        lock (gate)
        {
            if (syncTask is { IsCompleted: false }) return syncTask;
            return syncTask = Task.Run(RunSyncAsync);
        }
    }

    private async Task RunSyncAsync()
    {
        if (!link.Connected)
        {
            LastSyncError = "Hachidori is not linked. Open Chrome with Hachidori sharing, then sync.";
            return;
        }
        if (Engine() is null)
        {
            LastSyncError = EngineError;
            return;
        }
        try
        {
            LastSyncError = null;
            syncedRevisions = Revisions(link.Snapshot);
            var result = await Sync.SyncAsync(link.Snapshot.Count > 0 ? link.Snapshot : null, link.HostVersion,
                EngineVersion?.Hachidori ?? string.Empty, lifetime.Token).ConfigureAwait(false);
            LastSync = result;
            DiagnosticLog.Write("offline", $"Sync: {result}");
            if (result.Changed)
            {
                // Release the replaced folders' mappings, then delete them; one still
                // held is removed on the next start.
                Invalidate();
                foreach (var folder in result.Retired) OfflineStore.TryDelete(folder);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LastSyncError = ex.Message;
            DiagnosticLog.Write("offline", $"Sync failed: {ex.Message}");
            DalamudServices.Log.Warning(ex, "Offline dictionary sync failed");
        }
    }

    /// <summary>Turns offline mode off and deletes the local copy.</summary>
    public async Task RemoveAsync()
    {
        config.OfflineMode = false;
        config.Save();
        pendingSync?.Cancel();
        if (syncTask is { } running)
        {
            try { await running.ConfigureAwait(false); }
            catch (Exception) { /* reported by the sync itself */ }
        }
        Invalidate();
        Store.Clear();
        LastSync = null;
        LastSyncError = null;
    }

    // ------------------------------------------------------- following the host

    private void OnConnectionChanged()
    {
        if (link.Connected && Enabled) Schedule(ref pendingSync, SyncDelay, () => _ = SyncNowAsync());
    }

    private void OnSnapshotChanged()
    {
        if (!Enabled || !link.Connected) return;
        var snapshot = link.Snapshot;
        Schedule(ref pendingSave, SnapshotSaveDelay, () =>
        {
            try { Store.SaveSnapshot(link.Snapshot); }
            catch (Exception ex) { DiagnosticLog.Write("offline", $"Saving the settings snapshot failed: {ex.Message}"); }
        });
        // A dictionary added, removed or updated in Hachidori since the last sync:
        // fetch just that. Other storage changes (lookup counts, options) do not sync.
        if (syncedRevisions is not null && Revisions(snapshot) != syncedRevisions)
            Schedule(ref pendingSync, SyncDelay, () => _ = SyncNowAsync());
    }

    /// <summary>The host's packages and revisions, as one comparable string.</summary>
    private static string Revisions(JsonObject snapshot) => string.Join('\n',
        OfflineSync.SnapshotRows(snapshot).Values.Select(r => $"{Str(r["id"])}={Str(r["revision"])}").Order(StringComparer.Ordinal));

    private void Schedule(ref CancellationTokenSource? pending, TimeSpan delay, Action action)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Interlocked.Exchange(ref pending, next)?.Cancel();
        _ = Task.Delay(delay, next.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) action();
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- status

    /// <summary>Why offline answers may differ from live ones, for the settings window.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        if (EngineError is { } error) warnings.Add(error);
        var engineVersion = EngineVersion?.Hachidori;
        var host = link.Connected ? link.HostVersion : Store.Manifest.HostVersion;
        if (engineVersion is not null && OfflinePlan.IsNewer(host, engineVersion))
            warnings.Add($"Hachidori {host} is newer than the offline engine (built from {engineVersion}). Offline results may differ until the plugin is updated.");
        foreach (var skip in Store.Manifest.Skipped)
            warnings.Add($"Not available offline: {skip.Title}. {skip.Reason}");
        foreach (var (dictionary, reason) in LoadFailures)
            warnings.Add($"{dictionary.Title} did not load offline: {reason}");
        return warnings;
    }

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    public void Dispose()
    {
        link.ConnectionChanged -= OnConnectionChanged;
        link.SnapshotChanged -= OnSnapshotChanged;
        lifetime.Cancel();
        try { syncTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        lock (engineGate)
        lock (gate)
        {
            engine?.Dispose();
            engine = null;
        }
        http.Dispose();
    }
}
