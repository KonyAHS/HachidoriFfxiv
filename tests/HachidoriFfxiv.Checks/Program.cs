using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using HachidoriFfxiv.Ui;
using HachidoriFfxiv.Audio;
using HachidoriFfxiv;
using System.Net.Http.Json;
using NAudio.Wave;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

async Task Until(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, deadline.Token);
}

// The actual native popup scope, tested outside FFXIV against local cimgui.
// A style-stack underflow used to crash the game when the first popup opened.
CheckPopupThemes(Check);
await MediaChecks.RunAsync(Check);
await TrimChecks.RunAsync(Check);
CursorChecks.Run(Check);
CheckPopupScopes(Check);
PopupTheme.Current = ThemeFor("crystal", "crystal", "blue");
CheckPopupScopes((ok, name) => Check(ok, $"Crystal theme: {name}"));
PopupTheme.Current = PopupTheme.PluginDefault();
await NpcImageChecks.Run(Check);
await PublishChecks.Run(Check);
await OfflineChecks.Run(Check);
AnkiChecks.Run(Check);
StatsChecks.Run(Check);
await WebViewChecks.RunAsync(Check);
CheckFfxivDictionary(Check);

Vector2 FixedMeasure(string s) => new(s.EnumerateRunes().Count() * 10, 12);
var native = NativeTextLayout.Build("冒険\n者𠮷", 40, 40, 0, 12, 18, false, FixedMeasure);
Check(native.Hit(new(5, 4))?.Index == 0 && native.Hit(new(15, 4))?.Index == 1, "native hover resolves individual characters");
Check(native.Hit(new(15, 21))?.Index == 4 && native.Cells.Count == 4, "native multiline offsets preserve surrogate pairs");
Check(native.Hit(new(25, 4)) is null && native.Hit(new(5, 14)) is null, "native blank space and line gaps are not hover targets");
var centered = NativeTextLayout.Build("冒険", 100, 50, 4, 12, 18, false, FixedMeasure);
Check(centered.Hit(new(41, 20))?.Index == 0 && centered.Hit(new(5, 5)) is null, "native center alignment follows text rather than the whole node");
var wrapped = NativeTextLayout.Build("冒険者", 20, 40, 0, 12, 18, true, FixedMeasure);
Check(wrapped.Hit(new(5, 20))?.Index == 2, "native soft wrapping preserves source offsets");
var crlf = NativeTextLayout.Build("冒\r\n険", 40, 40, 0, 12, 18, false, FixedMeasure);
Check(crlf.Hit(new(5, 20))?.Index == 3, "native CRLF is one visual line break");
Check(!NativeTextLayout.Contains(new(0), new(20), new(20, 5)), "native hit rectangles exclude the far edge");
Check(native.HitScreen(new(210, 108), new(200, 100), new(2), new(200, 100), new(280, 180))?.Index == 0,
    "native targets follow moved text boxes and UI scaling");
Check(native.HitScreen(new(210, 108), new(600, 300), new(2), new(600, 300), new(680, 380)) is null,
    "old text-box coordinates stop matching after a move");
Check(native.HitScreen(new(205, 105), new(200, 100), new(2), new(212, 100), new(280, 180)) is null,
    "native clipped-away text is not hoverable");
Check(native.HitScreen(new(205, 105), new(200, 100), Vector2.Zero, new(200, 100), new(280, 180)) is null,
    "collapsed native transforms do not produce hover targets");
Check(NativeTextLayout.Build("冒険者", 40, 40, 0, 12, 18, true, FixedMeasure).Hit(new(25, 5))?.Index == 2,
    "native targets reflow when text-box width changes");

float MeasureChat(ReadOnlySpan<byte> bytes)
{
    var width = 0f;
    foreach (var payload in new ReadOnlySeStringSpan(bytes))
        if (payload.Type == ReadOnlySePayloadType.Text)
            width += FixedMeasure(Encoding.UTF8.GetString(payload.Body)).X;
        else if (payload.MacroCode == MacroCode.Icon)
            width += 14;
    return width;
}

byte[] chatText = [.."隠\r"u8, 0x02, (byte)MacroCode.Icon, 0x02, 0x02, 0x03, .."冒険\rabc\r\n者𠮷\r"u8];
var chatParts = NativeChatLayout.Build(chatText, 40, 54, 0, 18, 18, bottomAligned: true, MeasureChat);
Check(chatParts.Count == 2 && chatParts[0].Text == "冒険" && chatParts[1].Text == "者𠮷",
    "chat shows the newest rows in the scrolled buffer and ignores the final row terminator");
Check(chatParts[0].Layout.Hit(new(5, 4)) is null && chatParts[0].Layout.Hit(new(18, 4))?.Index == 0,
    "an inline chat icon occupies screen space without creating a lookup target");
Check(chatParts[1].Layout.Hit(new(15, 40))?.Index == 1 && chatParts[0].Layout.Hit(new(15, 40)) is null,
    "chat message offsets are local and keep their on-screen position");
byte[] mixedChat = [.."古い台詞\r　続き\rエスティニアン：冒険\r[CWLS1]<Player"u8,
    0x02, (byte)MacroCode.Icon, 0x02, 0x02, 0x03, .."World> こんにちは\r"u8];
var mixedChatParts = NativeChatLayout.Build(mixedChat, 400, 29, 0, 18, 18, bottomAligned: true, MeasureChat);
Check(mixedChatParts.Count == 2 && mixedChatParts[0].Text.StartsWith("エスティニアン", StringComparison.Ordinal)
    && mixedChatParts[0].Layout.HitScreen(new(5, 4), Vector2.Zero, Vector2.One, Vector2.Zero, new(400, 29)) is { Index: 0, Min.Y: 0, Max.Y: 11 }
    && mixedChatParts[1].Layout.HitScreen(new(229, 26), Vector2.Zero, Vector2.One, Vector2.Zero, new(400, 29))?.Index
        == mixedChatParts[1].Text.IndexOf('こ')
    && mixedChatParts[1].Layout.HitScreen(new(229, 29), Vector2.Zero, Vector2.One, Vector2.Zero, new(400, 29)) is null,
    "live chat clips the first visible row and keeps linkshell text aligned at the bottom");
var scrolledChat = NativeChatLayout.Build(mixedChat, 400, 29, 0, 18, 18, bottomAligned: false, MeasureChat);
Check(scrolledChat.Count == 2 && scrolledChat[0].Text == "古い台詞"
    && scrolledChat[0].Layout.Hit(new(5, 4))?.Index == 0 && scrolledChat[1].Text == "　続き"
    && scrolledChat[1].Layout.HitScreen(new(15, 28), Vector2.Zero, Vector2.One, Vector2.Zero, new(400, 29)) is { Index: 1, Max.Y: 29 },
    "scrolling into history shows the first buffered rows and clips the last visible row");
var sparseChat = NativeChatLayout.Build("冒険\r\r続き"u8, 40, 72, 0, 12, 18, bottomAligned: false, MeasureChat);
Check(sparseChat.Count == 2 && sparseChat[0].Layout.Hit(new(5, 4))?.Index == 0
    && sparseChat[1].Layout.Hit(new(5, 40))?.Index == 0,
    "chat preserves interior blank rows and the final row without a terminator");
var clippedChat = NativeChatLayout.Build(mixedChat, 400, 60, 0, 18, 18, bottomAligned: true, MeasureChat, visibleTop: 6, visibleHeight: 29);
Check(clippedChat.Count == 2 && clippedChat[1].Text.Contains("こんにちは")
    && clippedChat[1].Layout.HitScreen(new(229, 32), Vector2.Zero, Vector2.One, new(0, 6), new(400, 35))?.Index
        == clippedChat[1].Text.IndexOf('こ')
    && clippedChat[1].Layout.HitScreen(new(229, 36), Vector2.Zero, Vector2.One, new(0, 6), new(400, 35)) is null,
    "chat aligns bottom rows to the clipped visible viewport when parent ContainerNode clips ChatText");

var mineRequest = new JsonObject { ["captureUnavailable"] = new JsonArray("screenshot", "sentence-audio", "gif") };
var submit = HachidoriPayloads.Submit(mineRequest);
Check(submit["clientMedia"] is JsonObject { Count: 0 } && submit["request"] is JsonObject,
    "Anki submission always includes the required empty clientMedia envelope");
Check(mineRequest.Parent is null && mineRequest.Count == 1, "Anki envelope preserves the original request");
try
{
    HachidoriPayloads.Submit(new JsonObject { ["clientSpeech"] = new JsonObject() });
    throw new Exception("missing speech payload was accepted");
}
catch (HachidoriException ex) { Check(ex.Message.Contains("browser speech recording"), "browser speech plans produce an actionable error instead of an invalid envelope"); }
var segmentResponse = JsonNode.Parse("""{"segments":[{"id":0,"spans":[{"start":0,"length":3},{"start":3,"length":1},{"start":999,"length":1}]}]}""");
var words = WordSpan.Parse(segmentResponse, "冒険者さん");
Check(WordSpan.At(words, 2) == new WordSpan(0, 3), "hovering any character of a token resolves its word start and full range");
Check(words.Count == 2 && WordSpan.At(words, 4) is null, "token parser rejects invalid spans and preserves unsegmented gaps");
var imageBlocks = GlossaryBuilder.Build(JsonNode.Parse("""[{"type":"structured-content","content":{"tag":"img","path":"images/character.png","height":2,"sizeUnits":"em","alt":"character"}}]"""), 0, false, "FFXIV");
var imageSpec = imageBlocks.Single().Image!;
Check(imageSpec.Dictionary == "FFXIV" && imageSpec.Path == "images/character.png" && !imageBlocks.Single().IsEmpty,
    "glossary images retain dictionary identity and media path");
Check(imageSpec.Size(300, 20, 200, 100) == new Vector2(80, 40), "dictionary image sizing preserves its aspect ratio and em dimensions");
Check(DictionaryImage.Parse(new JsonObject { ["path"] = "../outside.png" }, "test") is null,
    "dictionary images reject non-dictionary paths");
Check(HachidoriPayloads.Media(new JsonObject { ["dataUrl"] = "data:image/png;base64,AQID" })!.SequenceEqual(new byte[] { 1, 2, 3 }),
    "dictionary media data URLs decode into image bytes");
Check(HachidoriPayloads.Audio(JsonNode.Parse("""{"audioMedia":[{"term":"different","reading":"","content":"AQID"}]}"""), "猫", "ねこ") is null,
    "audio selection never plays a different expression");
var logDirectory = Path.Combine(Path.GetTempPath(), "hachidori-ffxiv-checks-" + Guid.NewGuid().ToString("N"));
try
{
    DiagnosticLog.Write("test", "source failed https://user:secret@example.com/audio?key=secret");
    var logPath = DiagnosticLog.Export(logDirectory, new JsonObject { ["connected"] = true });
    var exported = File.ReadAllText(logPath);
    Check(!exported.Contains("secret") && JsonNode.Parse(exported)!["events"]!.AsArray().Any(e => e!.ToString().Contains("<url>")), "debug log export redacts source URLs and credentials");
}
finally { Directory.Delete(logDirectory, recursive: true); }

// These are the actual parser and glossary converter, without loading the game
// or invoking native ImGui. Building twice also catches JsonNode reparenting.
var glossary = JsonNode.Parse("""
[{"type":"structured-content","content":[
  {"tag":"ruby","content":["冒険者",{"tag":"rt","content":"ぼうけんしゃ"}]},
  {"tag":"ol","content":{"tag":"li","content":"adventurer"}},
  {"tag":"div","data":{"content":"attribution"},"content":"source credit"}
]}]
""")!;
var original = glossary.ToJsonString();
var blocks = GlossaryBuilder.Build(glossary, 0, false);
GlossaryBuilder.Build(glossary, 0, false);
Check(glossary.ToJsonString() == original, "glossary conversion preserves the source tree");
Check(blocks.Any(b => b.ScanText.Contains("adventurer")), "single-node list content renders");
Check(blocks.SelectMany(b => b.Runs).Any(r => r.Text == "冒険者" && r.Ruby == "ぼうけんしゃ"), "structured furigana survives conversion");
Check(blocks.All(b => !b.ScanText.Contains("source credit")), "attribution hides when disabled");
Check(GlossaryBuilder.Build(glossary, 0, true).Any(b => b.ScanText.Contains("source credit")), "attribution renders when enabled");
var ruby = Japanese.DistributeFurigana("分かる", "わかる");
Check(ruby.Count == 2 && ruby[0].Reading == "わ" && ruby[1].Reading == "", "furigana stays above kanji");
Check(NameKana.ToKatakana("Rony") == "ロニー" && NameKana.ToKatakana("Tamino") == "タミノ", "player names convert to katakana");
var nameOffset = 12;
var named = NameKana.Apply("ありがとう、Rony Tamino。Ronyさん", NameKana.Replacements("Rony Tamino", null), ref nameOffset);
Check(named == "ありがとう、ロニー・タミノ。ロニーさん" && nameOffset == 6, "player name replacement keeps the match offset on the name");
var afterOffset = 18;
NameKana.Apply("ありがとう、Rony Tamino。Ronyさん", NameKana.Replacements("Rony Tamino", null), ref afterOffset);
Check(afterOffset == 14, "player name replacement shifts later offsets");
Check(NameKana.Apply("Ronya", NameKana.Replacements("Rony Tamino", "ロニ・タミノ")) == "Ronya" && NameKana.Apply("Rony!", NameKana.Replacements("Rony Tamino", "ロニ・タミノ")) == "ロニ!", "player name override replaces whole words only");
var result = LookupResult.Parse(JsonNode.Parse("""
{"matched":"分かりませんでした","deinflected":"分かる","trace":[{"name":"-た"}],
 "term":{"expression":"分かる","reading":"わかる","glossaries":[{"dictionary":"fixture","glossary":"[\"understand\"]"}],
 "frequencies":[{"dictionary":"frequency","frequencies":[{"value":260,"displayValue":"260㋕"}]}]}}
""")!.AsObject());
Check(result.Matched == "分かりませんでした" && result.Trace.Single() == "-た" && result.Term.Frequencies.Single().Values.Single().Display == "260㋕", "lookup response preserves surface text, inflection and frequency display");
Check(result.Term.Glossaries.Single().Content is JsonArray, "JSON-encoded glossary parses");

if (args.Contains("--live"))
{
    using var live = new HachidoriLink(() => new Uri("ws://127.0.0.1:8771/link"));
    var reply = await live.RequestAsync("hoshidicts-offscreen", "hd_lookup",
        new JsonObject { ["text"] = "冒険者", ["maxResults"] = 4, ["scanLength"] = 16 });
    Check(reply?["results"] is JsonArray { Count: > 0 }, "live Hachidori lookup returns dictionary results");
    foreach (var item in reply!["results"]!.AsArray().OfType<JsonObject>())
        foreach (var entry in LookupResult.Parse(item).Term.Glossaries)
            GlossaryBuilder.Build(entry.Content, 0, false);
    Check(true, "live dictionary glossaries convert without errors");
    Console.WriteLine($"Host: {live.HostName} {live.HostVersion}; dictionaries: {live.DictionaryCount}");
    var segmentation = await live.RequestAsync("hoshidicts-offscreen", "hd_segment", HachidoriPayloads.Segment("冒険者さん、お待たせしました", 16, true));
    var liveWords = WordSpan.Parse(segmentation, "冒険者さん、お待たせしました");
    Check(liveWords.Any(w => w.Length > 1), "live host tokenizer returns word spans");

    var illustrated = await live.RequestAsync("hoshidicts-offscreen", "hd_lookup", new JsonObject { ["text"] = "エスティニアン", ["maxResults"] = 8, ["scanLength"] = 16 });
    var imageCount = 0;
    foreach (var item in illustrated!["results"]!.AsArray().OfType<JsonObject>())
    {
        foreach (var entry in LookupResult.Parse(item).Term.Glossaries)
        {
            foreach (var imageBlock in GlossaryBuilder.Build(entry.Content, 0, false, entry.Dictionary).Where(b => b.Image is not null))
            {
                var spec = imageBlock.Image!;
                var media = await live.RequestAsync("hoshidicts-offscreen", "hd_media", new JsonObject
                {
                    ["dictionary"] = spec.Dictionary, ["path"] = spec.Path, ["generation"] = illustrated["generation"]!.DeepClone(),
                });
                Check(HachidoriPayloads.Media(media) is { Length: > 0 }, "live dictionary image bytes load through hd_media");
                imageCount++;
                if (imageCount >= 2) break;
            }
        }
        if (imageCount >= 2) break;
    }
    Check(imageCount > 0, "live illustrated FFXIV entry contains renderable image metadata");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var audioBody = new StringContent("""{"text":"猫","type":"term","markers":["audio"],"maxEntries":2,"includeMedia":true}""", Encoding.UTF8, "application/json");
    using var audioReply = await http.PostAsync("http://127.0.0.1:19633/ankiFields", audioBody);
    audioReply.EnsureSuccessStatusCode();
    var audioBytes = HachidoriPayloads.Audio(JsonNode.Parse(await audioReply.Content.ReadAsStringAsync()), "猫", "ねこ");
    Check(audioBytes is { Length: > 0 }, "live pronunciation uses the Chrome host's configured audio sources");
    try
    {
        using var legacy = new StreamMediaFoundationReader(new MemoryStream(audioBytes!));
        Console.WriteLine("Legacy Media Foundation decoder opened the source on this machine.");
    }
    catch (Exception ex) { Console.WriteLine($"Legacy decoder failure reproduced: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
    using var decoded = PronunciationDecoder.Open(audioBytes!);
    var pcm = new byte[32768];
    Check(decoded.Read(pcm, 0, pcm.Length) > 0 && pcm.Any(b => b != 0), "live pronunciation decodes to non-silent PCM");
    if (args.Contains("--play-audio"))
    {
        decoded.Position = 0;
        using var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
        Exception? failure = null;
        output.PlaybackStopped += (_, e) => failure = e.Exception;
        output.Init(decoded);
        output.Play();
        await Task.Delay(1500);
        output.Stop();
        Check(failure is null, "live pronunciation plays through WASAPI without device/codec errors");
    }
}
else
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build();
    app.UseWebSockets();
    var protocol = 1;
    var connections = 0;
    var origins = new List<string>();
    app.Map("/link", async context =>
    {
        origins.Add(context.Request.Headers.Origin.ToString());
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref connections);
        try
        {
            await Receive(ws, context.RequestAborted);
            await Send(ws, new JsonObject { ["kind"] = "hello", ["protocol"] = protocol, ["name"] = "Test host", ["snapshot"] = new JsonObject() });
            while (ws.State == WebSocketState.Open)
            {
                var frame = await Receive(ws, context.RequestAborted);
                if (frame is null) break;
                var type = frame["message"]?["type"]?.ToString();
                if (type == "never-reply") continue;
                if (type == "storage-test")
                {
                    await Send(ws, JsonNode.Parse("""{"kind":"storage","changes":{"options":{"newValue":{"test":true}}}}""")!.AsObject());
                    await Send(ws, JsonNode.Parse("""{"kind":"storage","changes":{"options":{"oldValue":{"test":true}}}}""")!.AsObject());
                }
                JsonObject response = type == "error-test"
                    ? new() { ["ok"] = false, ["error"] = "Expected host error" }
                    : new() { ["ok"] = true, ["echo"] = frame["message"]?.DeepClone() };
                await Send(ws, new JsonObject { ["kind"] = "reply", ["id"] = frame["id"]?.DeepClone(), ["response"] = response }, fragment: true);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    });
    await app.StartAsync();
    var uri = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/link");
    using (var link = new HachidoriLink(() => uri, requestTimeout: TimeSpan.FromMilliseconds(600)))
    {
        await Until(() => link.Connected);
        Check(origins.All(o => o == "hoshi://hoshidicts"), "native relay Origin is sent");
        var payload = new JsonObject { ["text"] = "冒険者" };
        var reply = await link.RequestAsync("hoshidicts-offscreen", "hd_lookup", payload);
        Check(reply?["echo"]?["text"]?.ToString() == "冒険者" && payload.Count == 1, "fragmented UTF-8 reply and payload ownership");
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => link.RequestAsync("test", $"request-{i}")));
        Check(replies.Select((r, i) => r?["echo"]?["type"]?.ToString() == $"request-{i}").All(b => b), "concurrent requests correlate replies");
        var updates = 0;
        link.SnapshotChanged += () => Interlocked.Increment(ref updates);
        await link.RequestAsync("test", "storage-test");
        Check(updates == 2 && !link.Snapshot.ContainsKey("options"), "storage additions and removals apply");
        try { await link.RequestAsync("test", "error-test"); throw new Exception("host error was ignored"); }
        catch (HachidoriException ex) { Check(ex.Message == "Expected host error", "host errors surface to callers"); }
        try { await link.RequestAsync("test", "never-reply"); throw new Exception("request did not time out"); }
        catch (HachidoriException ex) { Check(ex.Message.Contains("did not reply in time"), "unanswered requests time out"); }
        using var cancelled = new CancellationTokenSource(50);
        try { await link.RequestAsync("test", "never-reply", ct: cancelled.Token); throw new Exception("request was not cancelled"); }
        catch (OperationCanceledException) { Check(true, "caller cancellation is preserved"); }
        var hanging = link.RequestAsync("test", "never-reply");
        link.Reconnect();
        try { await hanging; throw new Exception("disconnected request did not fail"); }
        catch (HachidoriException) { Check(true, "disconnect fails pending requests"); }
        await Until(() => connections >= 2 && link.Connected);
        Check((await link.RequestAsync("test", "after-reconnect"))?["ok"]?.GetValue<bool>() == true, "requests recover after reconnect");
        var disposing = link.RequestAsync("test", "never-reply");
        link.Dispose();
        try { await disposing; throw new Exception("disposed request did not end"); }
        catch (Exception ex) when (ex is OperationCanceledException or HachidoriException) { Check(true, "unload completes pending requests"); }
    }
    protocol = 999;
    using (var incompatible = new HachidoriLink(() => uri))
    {
        await Until(() => incompatible.Error?.Contains("handshake") == true);
        Check(!incompatible.Connected, "incompatible handshake never marks the link ready");
    }
    await app.StopAsync();
}
Console.WriteLine($"{passed} checks passed.");

static async Task<JsonObject?> Receive(WebSocket ws, CancellationToken token)
{
    var buffer = new byte[4096];
    using var stream = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(buffer, token);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonNode.Parse(stream.ToArray())?.AsObject();
}

static async Task Send(WebSocket ws, JsonObject frame, bool fragment = false)
{
    var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
    if (fragment)
    {
        await ws.SendAsync(bytes.AsMemory(0, bytes.Length / 2), WebSocketMessageType.Text, false, default);
        await ws.SendAsync(bytes.AsMemory(bytes.Length / 2), WebSocketMessageType.Text, true, default);
    }
    else await ws.SendAsync(bytes, WebSocketMessageType.Text, true, default);
}

static unsafe void CheckPopupScopes(Action<bool, string> check)
{
    var context = ImGui.CreateContext();
    try
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new(1280, 720);
        io.DeltaTime = 1f / 60;
        io.IniFilename = null;
        io.Fonts.AddFontDefault();
        io.Fonts.Build();
        for (var frame = 0; frame < 3; frame++)
        {
            ImGui.NewFrame();
            var styles = context.StyleVarStack.Size;
            var colors = context.ColorStack.Size;
            var windows = context.CurrentWindowStack.Size;
            using (var popup = new PopupWindowScope("regression-root", ImGuiWindowFlags.None, frame == 0))
            {
                if (popup.Visible)
                {
                    var block = new RichBlock().Add("Dictionary entry: ").Add(new RichRun { Text = "headword", Ruby = "reading", Chip = true });
                    RichText.Draw(block, 240, (0, 10), false);
                    var font = ImGui.GetFont();
                    var fontSize = ImGui.GetFontSize();
                    RichText.SetFontLadders(new() { [(nint)font.Handle] = [new(font, 1f), new(font, 0.5f), new(font, 1.7f)] });
                    check(MathF.Abs(RichText.Sized(fontSize * 0.6f).Size - fontSize * 0.5f) < 0.001f
                          && MathF.Abs(RichText.Sized(fontSize * 3f).Size - fontSize * 1.7f) < 0.001f,
                        $"text sizes snap to a rasterised font on frame {frame + 1}");
                    RichText.Draw(block, 240, (0, 10), false);
                    RichText.SetFontLadders(new());
                }
                using var nested = new PopupWindowScope("regression-nested", ImGuiWindowFlags.None, false);
            }
            check(context.StyleVarStack.Size == styles && context.ColorStack.Size == colors && context.CurrentWindowStack.Size == windows,
                $"popup styles, colors and windows balance on frame {frame + 1}");
            try
            {
                using var throwing = new PopupWindowScope("regression-error", ImGuiWindowFlags.None, false);
                throw new InvalidOperationException("simulated entry failure");
            }
            catch (InvalidOperationException) { }
            check(context.StyleVarStack.Size == styles && context.ColorStack.Size == colors && context.CurrentWindowStack.Size == windows,
                $"popup restores native stacks after a drawing exception on frame {frame + 1}");
            ImGui.Begin("regression-reader");
            ImGui.SetWindowFocus();
            var reader = ImGuiP.GetCurrentWindow();
            ImGui.End();
            ImGuiP.BringWindowToDisplayFront(reader);
            using (var popup = new PopupWindowScope("regression-order", ImGuiWindowFlags.NoFocusOnAppearing, false))
            {
                check(context.Windows[context.Windows.Size - 1] == ImGuiP.GetCurrentWindow() && context.NavWindow == reader,
                    $"dictionary stays above a focused reader without stealing focus on frame {frame + 1}");
                using var nested = new PopupWindowScope("regression-order-nested", ImGuiWindowFlags.NoFocusOnAppearing, false);
                check(context.Windows[context.Windows.Size - 1] == ImGuiP.GetCurrentWindow(),
                    $"nested dictionary stays above its parent on frame {frame + 1}");
            }
            ImGui.EndFrame();
        }
    }
    finally { ImGui.DestroyContext(context); }
}

static string ThemeCss(string renderer)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, "HachidoriFfxiv", "Themes", renderer + ".css");
        if (File.Exists(path)) return File.ReadAllText(path);
    }
    throw new FileNotFoundException($"Theme stylesheet {renderer}.css");
}

static PopupTheme ThemeFor(string renderer, string palette, string variant, string custom = "", float opacity = 85) =>
    PopupThemeEngine.Compute(new(renderer, palette, variant, ThemeCss(renderer), custom, opacity));

static void CheckPopupThemes(Action<bool, string> check)
{
    static bool Near(Vector4? color, string hex, float alpha = 1f) =>
        color is { } c && CssColor.Parse(hex, true) is { } want
        && Vector3.Distance(new(c.X, c.Y, c.Z), new(want.X, want.Y, want.Z)) < 0.02f && MathF.Abs(c.W - alpha) < 0.02f;

    var defaultTheme = ThemeFor("default", "default", "auto");
    check(defaultTheme.Dark && Near(defaultTheme.Popup.Background, "#272630", 0.85f) && defaultTheme.Popup.Radius == 14
          && Near(defaultTheme.Accent, "#a89ce4") && defaultTheme.Section is not null,
        "Default theme: palette colours, opacity, rounded popup and glossary cards");
    var light = ThemeFor("default", "solarized-light", "auto");
    check(!light.Dark && Near(light.Popup.Background, "#fdf6e3", 0.85f) && Near(light.Text, "#586e75"),
        "Default palettes switch the popup to light colours");
    var oklch = ThemeFor("default", "dark", "auto");
    check(oklch.Popup.Background is { X: > 0.05f and < 0.2f } && oklch.Text is { X: > 0.85f },
        "oklch() palette colours resolve");
    var nazeka = ThemeFor("nazeka", "nazeka", "auto");
    check(Near(nazeka.Headword, "#99ddff") && Near(nazeka.Reading, "#99ff99") && nazeka.Popup.Radius == 3,
        "Nazeka theme: headword and reading colours");
    var jl = ThemeFor("jl", "jl", "auto");
    check(Near(jl.Separator, "#ffffff") && Near(jl.Headword, "#d2691e") && Near(jl.Popup.Background, "#000000", 0.85f),
        "JL theme: separators, spelling colour and opacity variable");
    var grey = ThemeFor("crystal", "crystal", "grey");
    var blue = ThemeFor("crystal", "crystal", "blue");
    var beige = ThemeFor("crystal", "crystal", "beige");
    check(grey.Frame is { Inset: 3, BorderWidth: 2 } && grey.Crest && Near(grey.Accent, "#b8913f"),
        "Crystal theme: inner frame, crest and gold accent");
    check(Near(blue.Popup.Background, "#223e9c", 0.85f) && !beige.Dark && grey.Dark,
        "Crystal colour variants: Retro blue, light Beige, dark Grey");
    var custom = ThemeFor("jl", "jl", "auto", ":host { --hoshidicts-accent: #f0a; } .gsm-hoshidicts-popup { border-radius: 20px; border: 2px solid gold; } .jl-spelling { color: orange; }");
    check(Near(custom.Accent, "#ff00aa") && custom.Popup.Radius == 20 && custom.Popup.BorderWidth == 2
          && Near(custom.Popup.BorderColor, "gold") && Near(custom.Headword, "orange"),
        "custom popup CSS overrides theme variables and boxes");
    var states = ThemeFor("jl", "jl", "auto", ".jl-spelling:hover { color: red; } .gsm-hoshidicts-audio-menu button { background: red; }");
    check(Near(states.Headword, "#d2691e") && states.Button.W < 0.01f,
        "hover states and unrelated containers do not restyle the popup");
    check(Near(ThemeFor("plain", "plain", "auto", opacity: 40).Popup.Background, "#ffffff") && Near(ThemeFor("bee", "bee", "auto", opacity: 40).Popup.Background, "#fff7fc", 0.4f),
        "Hachidori's opacity reaches themes that use it");
    check(CssColor.Parse("color-mix(in srgb, #000 50%, transparent)", true) is { W: > 0.49f and < 0.51f }
          && CssColor.Parse("light-dark(#fff, #000)", false) is { X: 1 } && CssColor.Parse("rgb(255 0 0 / 0.5)", true) is { X: 1, W: 0.5f },
        "colour functions: color-mix, light-dark and modern rgb()");

    RichText.SetContrast(CssColor.ToAbgr(new Vector4(0.97f, 0.97f, 0.92f, 1)), 0xFF222222);
    var faint = RichText.ReadableColor(0xFFC8D8E8);
    RichText.SetContrast(CssColor.ToAbgr(new Vector4(0.1f, 0.1f, 0.12f, 1)), 0xFFEEEEEE);
    var kept = RichText.ReadableColor(0xFFC8D8E8);
    RichText.SetContrast(0, 0);
    check(faint != 0xFFC8D8E8 && kept == 0xFFC8D8E8, "dictionary colours are only darkened where they would vanish");
}

static void CheckFfxivDictionary(Action<bool, string> check)
{
    check(FfxivDictionary.IsAllowedExternalUrl(FfxivDictionary.DownloadUrl)
          && FfxivDictionary.IsAllowedExternalUrl(FfxivDictionary.GuideUrl)
          && !FfxivDictionary.IsAllowedExternalUrl("file:///C:/Windows/notepad.exe")
          && !FfxivDictionary.IsAllowedExternalUrl("javascript:alert(1)")
          && !FfxivDictionary.TryOpenUrl("file:///C:/Windows/notepad.exe", out _),
        "FFXIV dictionary URLs allow only http/https links");

    var live = FfxivDictionary.FindInstalled([
        new JsonObject { ["title"] = "Jitendex.org", ["revision"] = "2026-01-01", ["enabled"] = true },
        new JsonObject { ["title"] = "FFXIV Hachidori JP–EN", ["revision"] = "2026.11.22.1", ["enabled"] = true },
    ]);
    check(live is { Title: "FFXIV Hachidori JP–EN", Revision: "2026.11.22.1", Enabled: true },
        "FFXIV dictionary detection finds installed enabled dictionary");

    var disabledHyphen = FfxivDictionary.FindInstalled([
        new JsonObject { ["title"] = "FFXIV Hachidori JP-EN", ["revision"] = "2026.10.14.1", ["enabled"] = false },
    ]);
    check(disabledHyphen is { Revision: "2026.10.14.1", Enabled: false },
        "FFXIV dictionary detection matches ASCII hyphen and disabled state");

    var offline = FfxivDictionary.FindInstalledOffline([
        new HachidoriFfxiv.Offline.OfflineDictionary
        {
            Id = "d1", Title = "FFXIV Hachidori JP–EN", Revision = "2026.11.22.1", Folder = "f1",
        },
    ]);
    check(offline is { Title: "FFXIV Hachidori JP–EN", Revision: "2026.11.22.1", Enabled: true },
        "FFXIV dictionary detection finds offline synced dictionary");

    check(FfxivDictionary.FindInstalled([new JsonObject { ["title"] = "JMdict" }]) is null,
        "FFXIV dictionary detection ignores unrelated dictionaries");
}
