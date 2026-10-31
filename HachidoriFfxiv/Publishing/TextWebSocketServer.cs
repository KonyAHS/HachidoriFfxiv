using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Publishing;

/// <summary>
/// A loopback-only WebSocket text feed. Built on TcpListener because HttpListener needs a URL
/// reservation for some hosts and rejects a Host header that differs from its prefix.
/// </summary>
public sealed class TextWebSocketServer : IDisposable
{
    private const int MaxClients = 8;
    private const int MaxHandshakeBytes = 16 * 1024;
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Client, byte> clients = new();
    private readonly TcpListener[] listeners;

    private sealed record Client(WebSocket Socket, Channel<string> Queue);

    /// <summary>Binds immediately, so a port already in use throws here.</summary>
    public TextWebSocketServer(int port)
    {
        var v4 = Bind(IPAddress.Loopback, port);
        TcpListener? v6 = null;
        try { v6 = Bind(IPAddress.IPv6Loopback, port); }
        catch (SocketException) { } // Clients resolving localhost fall back to IPv4.
        listeners = v6 is null ? [v4] : [v4, v6];
        foreach (var listener in listeners)
            _ = AcceptAsync(listener);
    }

    public int ClientCount => clients.Count;

    public void Broadcast(string message)
    {
        foreach (var client in clients.Keys)
            client.Queue.Writer.TryWrite(message);
    }

    /// <summary>A website can open a WebSocket to localhost; only local pages and non-browsers may read game text.</summary>
    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private static TcpListener Bind(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        listener.Start();
        return listener;
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var connection = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
                _ = ServeAsync(connection);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (!stopping.IsCancellationRequested) { }
        }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        {
            Client? client = null;
            try
            {
                var token = stopping.Token;
                var stream = connection.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                var key = Header(request, "Sec-WebSocket-Key");
                string response;
                if (key is null || !string.Equals(Header(request, "Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase))
                    response = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n";
                else if (!IsAllowedOrigin(Header(request, "Origin")) || clients.Count >= MaxClients)
                    response = "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n";
                else
                    response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                        + $"Sec-WebSocket-Accept: {AcceptKey(key)}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token).ConfigureAwait(false);
                if (!response.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                    return;

                var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
                var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
                client = new Client(socket, queue);
                clients[client] = 0;
                using var closed = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sending = SendAsync(client, closed.Token);
                try { await ReceiveUntilClosedAsync(socket, closed.Token).ConfigureAwait(false); }
                finally
                {
                    closed.Cancel();
                    await sending.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or SocketException or System.IO.IOException or ObjectDisposedException) { }
            finally
            {
                if (client is not null)
                {
                    clients.TryRemove(client, out _);
                    client.Socket.Dispose();
                }
            }
        }
    }

    private static async Task SendAsync(Client client, CancellationToken ct)
    {
        try
        {
            await foreach (var message in client.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await client.Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or System.IO.IOException or ObjectDisposedException) { }
    }

    /// <summary>Clients send nothing useful, but reading is what answers their pings and closes.</summary>
    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        // Byte by byte: anything after the header block belongs to the WebSocket.
        var bytes = new byte[MaxHandshakeBytes];
        var one = new byte[1];
        var length = 0;
        while (length < bytes.Length)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                break;
            bytes[length++] = one[0];
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                break;
        }
        return Encoding.ASCII.GetString(bytes, 0, length);
    }

    private static string? Header(string request, string name)
    {
        foreach (var line in request.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));

    public void Dispose()
    {
        stopping.Cancel();
        foreach (var listener in listeners)
            listener.Stop();
        foreach (var client in clients.Keys)
            client.Socket.Abort();
    }
}
