using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>
/// A linked Hachidori over the Hachidori Relay (Anki add-on) sharing protocol v1.
/// The relay accepts native hoshidicts apps on <c>/link</c> with the Origin <c>hoshi://hoshidicts</c>;
/// every request is forwarded to the sharing Hachidori, which answers it with its own
/// dictionaries, settings, lookup statistics and Anki Templates.
/// See hachidori extension/sharing-protocol.js and sharing-client.js.
/// </summary>
public sealed class HachidoriLink : IDisposable
{
    public const int ProtocolVersion = 1;
    private const string NativeOrigin = "hoshi://hoshidicts";
    private static readonly string[] Capabilities = ["linked-anki-v1", "linked-anki-v2", "linked-import-v1"];

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Func<Uri> address;
    private readonly Action<Exception>? logFrameError;
    private readonly TimeSpan requestTimeout;
    private ClientWebSocket? socket;
    private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextId;
    private int attempt;

    public HachidoriLink(Func<Uri> address, Action<Exception>? logFrameError = null, TimeSpan? requestTimeout = null)
    {
        this.address = address;
        this.logFrameError = logFrameError;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _ = Task.Run(RunAsync);
    }

    public bool Connected { get; private set; }
    public string? Error { get; private set; }
    public string HostName { get; private set; } = string.Empty;
    public string HostVersion { get; private set; } = string.Empty;
    public int DictionaryCount { get; private set; }
    public string[] HostCapabilities { get; private set; } = [];

    /// <summary>The host's shared storage (options, dictionaryState, lookupStats, ...).</summary>
    public JsonObject Snapshot { get; private set; } = new();

    public event Action? SnapshotChanged;

    /// <summary>Hachidori's Anki index revision changed (notes added, statuses changed).</summary>
    public event Action? WordStatusChanged;

    public void Reconnect()
    {
        try
        {
            socket?.Abort();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Sends one runtime message (<c>{target, type, ...}</c>) to the host and returns its reply.</summary>
    public async Task<JsonNode?> RequestAsync(string target, string type, JsonObject? payload = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(requestTimeout);
        var requestToken = deadline.Token;
        if (!Connected)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await ready.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HachidoriException(Error ?? "Hachidori is not reachable. Is Anki open with the Hachidori Relay add-on, and is Hachidori sharing?");
            }
        }

        var id = Interlocked.Increment(ref nextId);
        var message = payload?.DeepClone().AsObject() ?? new JsonObject();
        message["target"] = target;
        message["type"] = type;
        message["requestId"] = $"hachidori-ffxiv-{id}";
        var frame = new JsonObject
        {
            ["kind"] = "request",
            ["id"] = id,
            ["message"] = message,
        };

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        using var registration = requestToken.Register(() =>
        {
            if (pending.TryRemove(id, out var removed))
                removed.TrySetCanceled(requestToken);
        });

        try
        {
            await SendAsync(frame.ToJsonString(), requestToken).ConfigureAwait(false);
            var response = await tcs.Task.ConfigureAwait(false);
            if (response is JsonObject obj)
            {
                if (obj["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var ok) && !ok)
                    throw new HachidoriException(obj["error"]?.ToString() ?? "Hachidori reported an error.");
                if (obj["ok"] is null && obj["error"] is JsonValue err && err.TryGetValue<string>(out var errText) && !string.IsNullOrEmpty(errText))
                    throw new HachidoriException(errText);
            }

            DiagnosticLog.Write("relay", $"{type}: reply received");
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            DiagnosticLog.Write("relay", $"{type}: response timed out");
            throw new HachidoriException("Hachidori did not reply in time. Try again; check the host before retrying an Anki save.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("relay", $"{type}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var current = socket ?? throw new HachidoriException("Hachidori is not reachable.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await current.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            socket = ws;
            ws.Options.SetRequestHeader("Origin", NativeOrigin);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                connectDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(address(), connectDeadline.Token).ConfigureAwait(false);
                var hello = new JsonObject
                {
                    ["kind"] = "hello",
                    ["protocol"] = ProtocolVersion,
                    ["version"] = typeof(HachidoriLink).Assembly.GetName().Version?.ToString() ?? "0.1.0.0",
                    ["name"] = "Hachidori for FFXIV",
                    ["capabilities"] = new JsonArray(Array.ConvertAll(Capabilities, c => (JsonNode)JsonValue.Create(c)!)),
                };
                await SendAsync(hello.ToJsonString(), lifetime.Token).ConfigureAwait(false);
                await ReceiveLoopAsync(ws, connectDeadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("relay", $"Connection: {ex.Message}");
                Error ??= ex is WebSocketException
                    ? "Hachidori Relay is not running. Open Anki with the Hachidori Relay add-on."
                    : ex.Message;
            }
            finally
            {
                OnDisconnected(ws);
            }

            var delay = Math.Min(10_000, 500 * (1 << Math.Min(attempt, 5)));
            attempt++;
            try
            {
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnDisconnected(ClientWebSocket ws)
    {
        if (socket == ws)
            socket = null;
        if (Connected)
            Error ??= "Hachidori is not reachable.";
        Connected = false;
        if (ready.Task.IsCompleted)
            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var key in pending.Keys)
        {
            if (pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new HachidoriException("The linked Hachidori is not reachable."));
        }

        ws.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationTokenSource handshakeDeadline)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, handshakeDeadline.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                await HandleFrameAsync(ws, text).ConfigureAwait(false);
                if (Connected)
                    handshakeDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                logFrameError?.Invoke(ex);
            }
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string text)
    {
        if (JsonNode.Parse(text) is not JsonObject frame)
            return;
        switch (frame["kind"]?.GetValue<string>())
        {
            case "hello":
                if (frame["protocol"]?.ToString() != ProtocolVersion.ToString() || frame["snapshot"] is not JsonObject)
                {
                    Error = "Unsupported or malformed Hachidori sharing handshake. Update Hachidori and the relay.";
                    ws.Abort();
                    return;
                }
                HostName = frame["name"]?.ToString() ?? string.Empty;
                HostVersion = frame["version"]?.ToString() ?? string.Empty;
                DictionaryCount = frame["dictionaryCount"] is JsonValue dc && dc.TryGetValue<int>(out var count) ? count : 0;
                DiagnosticLog.Write("relay", $"Host connected: version={HostVersion}, dictionaries={DictionaryCount}");
                HostCapabilities = frame["capabilities"] is JsonArray caps
                    ? caps.Select(c => c?.ToString() ?? string.Empty).ToArray()
                    : [];
                Snapshot = frame["snapshot"] as JsonObject is { } snapshot ? (JsonObject)snapshot.DeepClone() : new JsonObject();
                Connected = true;
                Error = null;
                attempt = 0;
                ready.TrySetResult();
                SnapshotChanged?.Invoke();
                WordStatusChanged?.Invoke();
                break;
            case "reply":
                var idNode = frame["id"];
                long id = idNode is JsonValue v && v.TryGetValue<long>(out var l) ? l : long.TryParse(idNode?.ToString(), out var p) ? p : -1;
                if (pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(frame["response"]?.DeepClone());
                break;
            case "storage":
                if (frame["changes"] is JsonObject changes)
                {
                    var next = (JsonObject)Snapshot.DeepClone();
                    foreach (var (key, value) in changes)
                    {
                        // A storage batch carries chrome.storage changes ({newValue, oldValue}) or plain values.
                        var newValue = value is JsonObject change && (change.ContainsKey("newValue") || change.ContainsKey("oldValue"))
                            ? change["newValue"]
                            : value;
                        if (newValue is null)
                            next.Remove(key);
                        else
                            next[key] = newValue.DeepClone();
                    }

                    Snapshot = next;
                    SnapshotChanged?.Invoke();
                }

                break;
            case "word-status":
                WordStatusChanged?.Invoke();
                break;
            case "ping":
                await SendAsync("{\"kind\":\"pong\"}", lifetime.Token).ConfigureAwait(false);
                break;
            case "bye":
                Error = frame["reason"]?.ToString() is { Length: > 0 } reason ? reason : "Hachidori refused the connection.";
                // The receive loop must keep running to observe the peer's close frame.
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, lifetime.Token).ConfigureAwait(false);
                break;
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        try
        {
            socket?.Abort();
        }
        catch
        {
            // ignored
        }
    }
}

public sealed class HachidoriException(string message) : Exception(message);
