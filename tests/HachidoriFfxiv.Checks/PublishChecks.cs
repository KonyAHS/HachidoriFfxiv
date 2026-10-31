using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
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

        var json = JsonNode.Parse(PublishMessage.Format(PublishMessage.Line("ウリエンジェ", "123", true)))!;
        check(json["sentence"]!.GetValue<string>() == "ウリエンジェ：123", "messages are JSON objects with an optional speaker");
        check(PublishMessage.Format(PublishMessage.Line("名前", "冒険", false)) == """{"sentence":"冒険"}""", "speaker is omitted by default and Japanese stays readable");

        check(TextWebSocketServer.IsAllowedOrigin(null) && TextWebSocketServer.IsAllowedOrigin("http://localhost:7275")
            && TextWebSocketServer.IsAllowedOrigin("http://127.0.0.1:55000"), "non-browser and local clients may connect");
        check(!TextWebSocketServer.IsAllowedOrigin("https://example.com") && !TextWebSocketServer.IsAllowedOrigin("null")
            && !TextWebSocketServer.IsAllowedOrigin("http://localhost.example.com"), "websites may not read the feed");

        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new TextWebSocketServer(port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://{host}:{port}"), timeout.Token);
            while (server.ClientCount == 0) await Task.Delay(5, timeout.Token);
            server.Broadcast("""{"sentence":"冒険者"}""");
            var buffer = new byte[256];
            var received = await client.ReceiveAsync(buffer, timeout.Token);
            check(Encoding.UTF8.GetString(buffer, 0, received.Count) == """{"sentence":"冒険者"}""" && received.MessageType == WebSocketMessageType.Text,
                $"a client on {host} receives published text");
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            while (server.ClientCount != 0) await Task.Delay(5, timeout.Token);
        }

        using (var website = new ClientWebSocket())
        {
            website.Options.SetRequestHeader("Origin", "https://example.com");
            var refused = false;
            try { await website.ConnectAsync(new Uri($"ws://localhost:{port}"), timeout.Token); }
            catch (WebSocketException) { refused = true; }
            check(refused, "the server refuses a website's connection");
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
        check(reconnect.State == WebSocketState.Open, "the port can be reopened while an old client lingers");
    }
}
