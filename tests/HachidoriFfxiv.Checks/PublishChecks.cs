using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using HachidoriFfxiv.Publishing;

internal static class PublishChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var t = new DateTime(2026, 1, 1);
        var gate = new PublishGate(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        gate.Offer("Talk", "冒", t);
        gate.Offer("Talk", "冒険", t.AddMilliseconds(50));
        check(gate.Drain(t.AddMilliseconds(100)).Count == 0, "a line still changing is held back");
        check(gate.Drain(t.AddMilliseconds(150)).SequenceEqual(["冒険"]), "a typewriter line publishes once, complete");
        gate.Offer("Talk", "冒険", t.AddMilliseconds(500));
        check(gate.Drain(t.AddMilliseconds(700)).Count == 0, "a redraw of the same line is not repeated");
        gate.Offer("Talk", "冒険", t.AddSeconds(5));
        check(gate.Drain(t.AddSeconds(6)).SequenceEqual(["冒険"]), "the same line said again later publishes again");
        gate.Offer("Talk", "一行目", t.AddSeconds(10));
        gate.Offer("Talk", "二行目", t.AddSeconds(10).AddMilliseconds(10));
        check(gate.Drain(t.AddSeconds(10).AddMilliseconds(20)).SequenceEqual(["一行目"]), "a quickly skipped line is not lost");
        gate.Offer("Talk", "はい", t.AddSeconds(20));
        gate.Offer("Popup", "はい", t.AddSeconds(20));
        check(gate.Drain(t.AddSeconds(21)).Count(l => l == "はい") == 2, "sources are tracked independently");

        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var server = new TextWebSocketServer(port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var (host, origin, includeSpeaker, expected) in new[]
        {
            ("localhost", "http://localhost:7275", false, """{"sentence":"冒険者"}"""),
            ("127.0.0.1", "http://127.0.0.1:55000", true, """{"sentence":"ウリエンジェ：冒険者"}"""),
        })
        {
            using var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", origin);
            await client.ConnectAsync(new Uri($"ws://{host}:{port}"), timeout.Token);
            await CheckDeliveryAsync(server, client, includeSpeaker, expected, $"a client from {origin} receives Japanese text with the configured speaker setting");
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            while (server.ClientCount != 0) await Task.Delay(5, timeout.Token);
        }

        foreach (var origin in new[] { "https://example.com", "null", "http://localhost.example.com" })
        {
            using var website = new ClientWebSocket();
            website.Options.SetRequestHeader("Origin", origin);
            var refused = false;
            try { await website.ConnectAsync(new Uri($"ws://localhost:{port}"), timeout.Token); }
            catch (WebSocketException) { refused = true; }
            check(refused, $"the server rejects connections with Origin {origin}");
        }

        var busy = false;
        try { new TextWebSocketServer(port).Dispose(); }
        catch (SocketException) { busy = true; }
        check(busy, "a port in use is reported");

        using var held = new ClientWebSocket();
        await held.ConnectAsync(new Uri($"ws://localhost:{port}"), timeout.Token);
        server.Dispose();
        using var restarted = new TextWebSocketServer(port);
        using var reconnect = new ClientWebSocket();
        await reconnect.ConnectAsync(new Uri($"ws://localhost:{port}"), timeout.Token);
        await CheckDeliveryAsync(restarted, reconnect, false, """{"sentence":"冒険者"}""",
            "a client without an Origin receives text after the server restarts with an old client connected");

        async Task CheckDeliveryAsync(TextWebSocketServer publisher, ClientWebSocket client, bool includeSpeaker, string expected, string name)
        {
            while (publisher.ClientCount == 0) await Task.Delay(5, timeout.Token);
            publisher.Broadcast(PublishMessage.Format(PublishMessage.Line("ウリエンジェ", "冒険者", includeSpeaker)));
            var buffer = new byte[256];
            var received = await client.ReceiveAsync(buffer, timeout.Token);
            check(received.MessageType == WebSocketMessageType.Text && received.EndOfMessage
                  && Encoding.UTF8.GetString(buffer, 0, received.Count) == expected, name);
        }
    }
}
