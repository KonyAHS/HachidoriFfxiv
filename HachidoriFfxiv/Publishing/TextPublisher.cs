using System;
using Dalamud.Plugin.Services;
using HachidoriFfxiv.Capture;

namespace HachidoriFfxiv.Publishing;

/// <summary>Opt-in feed of captured game text for GSM. The server only exists while enabled.</summary>
public sealed class TextPublisher : IDisposable
{
    private readonly Configuration config;
    private readonly TextCapture capture;
    private readonly PublishGate gate = new();
    private TextWebSocketServer? server;
    private int serverPort;
    private DateTime retryAt;

    public TextPublisher(Configuration config, TextCapture capture)
    {
        this.config = config;
        this.capture = capture;
        capture.LineCaptured += OnLine;
        Plugin.Framework.Update += OnUpdate;
    }

    public bool Listening => server is not null;
    public int ClientCount => server?.ClientCount ?? 0;
    public string? Error { get; private set; }

    public void SendTest() => server?.Broadcast(PublishMessage.Format("これはHachidoriからのテストです。"));

    private void OnLine(GameLine line)
    {
        var wanted = line.Source switch
        {
            LineSource.Talk => config.PublishTalk,
            LineSource.BattleTalk => config.PublishBattleTalk,
            LineSource.Popup => config.PublishPopups,
            LineSource.Chat => config.PublishChat,
            _ => false,
        };
        if (config.PublishEnabled && wanted)
            gate.Offer(line.Source.ToString(), PublishMessage.Line(line.Speaker, line.Text, config.PublishSpeaker), line.Time);
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (server is not null && (!config.PublishEnabled || serverPort != config.PublishPort))
            Stop();
        if (!config.PublishEnabled)
            Error = null;
        else if (server is null && now >= retryAt)
            Start(now);
        foreach (var line in gate.Drain(now))
            server?.Broadcast(PublishMessage.Format(line));
    }

    private void Start(DateTime now)
    {
        try
        {
            server = new TextWebSocketServer(config.PublishPort);
            serverPort = config.PublishPort;
            Error = null;
            DiagnosticLog.Write("publish", $"Listening on port {serverPort}.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            retryAt = now.AddSeconds(5);
            var message = $"Port {config.PublishPort} is unavailable ({ex.SocketErrorCode}). Another program may be using it.";
            if (message != Error)
                DiagnosticLog.Write("publish", message);
            Error = message;
        }
    }

    private void Stop()
    {
        server?.Dispose();
        server = null;
        retryAt = default;
        DiagnosticLog.Write("publish", "Stopped.");
    }

    public void Dispose()
    {
        capture.LineCaptured -= OnLine;
        Plugin.Framework.Update -= OnUpdate;
        server?.Dispose();
        server = null;
    }
}
