// Live-versus-offline parity for Hachidori for FFXIV's offline mode.
//
// Needs Anki with the Hachidori Relay add-on and Chrome with Hachidori sharing.
// Syncs the host's dictionaries into a store (only what changed since the last
// run), loads them in the local engine as the plugin does, then sends the same
// hd_segment, hd_lookup (at every character of every line) and hd_kanji
// requests to both and diffs the JSON. Exits 1 on any difference.
//
//   dotnet run -c Release --project tests/HachidoriFfxiv.Parity [--store DIR] [--lines FILE] [--verbose]
//   ... --show TEXT        both result lists for one lookup
//   ... --audio TERM|READING  offline pronunciation from the saved audio sources
//
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Offline;

Console.OutputEncoding = Encoding.UTF8;
var store = Argument("--store") ?? Path.Combine(Path.GetTempPath(), "hachidori-ffxiv-parity");
var linesFile = Argument("--lines");
var verbose = args.Contains("--verbose");
const int MaxResults = 16;
const int ScanLength = 16;

string[] lines = linesFile is not null
    ? File.ReadAllLines(linesFile).Where(l => l.Trim().Length > 0).ToArray()
    :
    [
        "冒険者さん、ちょっと待ってくれないか？",
        "暁の血盟の皆さんが、あなたを待っていました。",
        "このクリスタルには、かつての記憶が宿っているようです。",
        "ウルダハの商人たちは、金のためなら何でもするだろう。",
        "お前たちに、この世界の真実を教えてやろう。",
        "食べられなかったので、分かりませんでした。",
        "二十四時間以内に、今日中に終わらせる。",
        "日本語を勉強している。",
        "ヤ・シュトラとアルフィノがリムサ・ロミンサへ向かった。",
        "ナナモ・ウル・ナモ様は、景色を眺めておられた。",
        "飛空艇に乗って、イシュガルドへ行ってきます！",
        "蛮神を討伐するには、光の加護が必要だ。",
        "ふたりで行こう。２人ならきっと大丈夫。",
        "思い出せないことばかりで、申し訳ありません……",
        "見て見ぬふりをするわけにはいかない。",
    ];

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// ---------------------------------------------------------------- link + sync

using var link = new HachidoriLink(() => new Uri("ws://127.0.0.1:8771/link"), ex => Console.Error.WriteLine(ex.Message));
var deadline = Stopwatch.StartNew();
while (!link.Connected && deadline.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100);
if (!link.Connected)
{
    Console.Error.WriteLine($"Hachidori is not linked: {link.Error}");
    return 2;
}
Console.WriteLine($"Linked to Hachidori {link.HostVersion} in {link.HostName}, {link.DictionaryCount} dictionaries.");

using var native = NativeEngine.LoadFrom(AppContext.BaseDirectory);
Console.WriteLine($"Offline engine: {native.Version.Short}");
var offlineStore = new OfflineStore(store);
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var sync = new OfflineSync(offlineStore, http, () => new Uri("http://127.0.0.1:19633/"), native.Version.DictionaryFormat);
var watch = Stopwatch.StartNew();
var synced = await sync.SyncAsync(link.Snapshot, link.HostVersion, native.Version.Hachidori, CancellationToken.None);
Console.WriteLine($"Sync: {synced} in {watch.Elapsed.TotalSeconds:0.0} s; {offlineStore.DiskBytes / (1 << 20)} MB in {store}");
foreach (var skip in synced.Skipped) Console.WriteLine($"  skipped {skip.Title}: {skip.Reason}");
foreach (var folder in synced.Retired) OfflineStore.TryDelete(folder);

var engine = new LocalEngine(native, () => KanjiFurigana.Shared);
watch.Restart();
engine.Load(OfflinePlan.For(offlineStore, link.Snapshot));
Console.WriteLine($"Loaded {engine.Loaded.Count} dictionaries ({engine.DictionaryCount} kinds) in {watch.ElapsedMilliseconds} ms.");
foreach (var (dictionary, error) in engine.Failures) Console.WriteLine($"  FAILED {dictionary.Title}: {error}");

// ------------------------------------------------------------------- compare

var hostOptions = link.Snapshot["options"] as JsonObject ?? new JsonObject();
var personal = hostOptions["personalDictionaryEnabled"] is not JsonValue p || !p.TryGetValue<bool>(out var on) || on;
var lookupOptions = HachidoriPayloads.LookupOptions(personal, hostOptions);
int compared = 0, differences = 0;
double liveMs = 0, localMs = 0;
var images = new HashSet<(string Dictionary, string Path)>();

void Compare(string what, JsonNode? live, JsonNode? local)
{
    compared++;
    if (JsonNode.DeepEquals(live, local)) return;
    differences++;
    Console.WriteLine($"DIFF {what}");
    var (path, left, right) = FirstDifference(live, local, "$");
    Console.WriteLine($"  at {path}\n  live:    {Clip(left)}\n  offline: {Clip(right)}");
}

async Task<JsonNode?> Live(string type, JsonObject message)
{
    var timer = Stopwatch.StartNew();
    var reply = await link.RequestAsync("hoshidicts-offscreen", type, message);
    liveMs += timer.Elapsed.TotalMilliseconds;
    return reply;
}

T Local<T>(Func<T> call)
{
    var timer = Stopwatch.StartNew();
    var reply = call();
    localMs += timer.Elapsed.TotalMilliseconds;
    return reply;
}

if (Argument("--audio") is { } audioTerm)
{
    // Offline pronunciation straight from the saved Audio URL / Yomitan JSON sources.
    var parts = audioTerm.Split('|');
    var bytes = await OfflineAudio.FetchAsync(http, (link.Snapshot["options"] as JsonObject)?["audioSources"], parts[0],
        parts.Length > 1 ? parts[1] : parts[0], CancellationToken.None);
    Console.WriteLine(bytes is null ? "offline audio: none" : $"offline audio: {bytes.Length} bytes, starts {Convert.ToHexString(bytes.AsSpan(0, Math.Min(4, bytes.Length)))}");
    return bytes is null ? 1 : 0;
}

if (Argument("--show") is { } shown)
{
    // Both result lists side by side, for investigating a difference.
    var message = HachidoriPayloads.Lookup(shown, MaxResults, ScanLength, lookupOptions);
    var liveList = (await Live("hd_lookup", message))?["results"] as JsonArray ?? [];
    var localList = engine.Lookup(message)["results"] as JsonArray ?? [];
    for (var i = 0; i < Math.Max(liveList.Count, localList.Count); i++)
        Console.WriteLine($"{i,2} {Describe(i < liveList.Count ? liveList[i] : null),-60} | {Describe(i < localList.Count ? localList[i] : null)}");
    return 0;
}

static string Describe(JsonNode? r) => r is null ? "-" :
    $"{r["matched"]} {r["term"]?["expression"]}【{r["term"]?["reading"]}】s={r["term"]?["score"]} g={(r["term"]?["glossaries"] as JsonArray)?.Count} {string.Join(",", (r["term"]?["glossaries"] as JsonArray ?? []).Select(g => g?["dictionary"]?.ToString()?[..3]))}";

foreach (var line in lines)
{
    if (verbose) Console.WriteLine($"line: {line}");
    var segment = HachidoriPayloads.Segment(line, ScanLength, personal);
    var liveSegment = await Live("hd_segment", segment);
    Compare($"hd_segment \"{line}\"", Pick(liveSegment, "segments"), Pick(Local(() => engine.Segment(segment)), "segments"));

    for (var start = 0; start < line.Length; start++)
    {
        if (char.IsLowSurrogate(line[start])) continue;
        var text = line[start..];
        var message = HachidoriPayloads.Lookup(text, MaxResults, ScanLength, lookupOptions);
        var liveReply = await Live("hd_lookup", message);
        var localReply = Local(() => engine.Lookup(message));
        Compare($"hd_lookup \"{text}\"", Pick(liveReply, "results", "dictionaryCount"), Pick(localReply, "results", "dictionaryCount"));
        CollectImages(liveReply);
    }

    foreach (var rune in line.EnumerateRunes().Where(r => r.Value is >= 0x4E00 and <= 0x9FFF).Distinct())
    {
        var character = rune.ToString();
        var liveKanji = await Live("hd_kanji", HachidoriPayloads.Kanji(character));
        Compare($"hd_kanji {character}", Pick(liveKanji, "kanji"), Pick(Local(() => engine.Kanji(HachidoriPayloads.Kanji(character))), "kanji"));
    }
}

// Dictionary images named by the results: the same bytes from both.
foreach (var (dictionary, path) in images.Take(8))
{
    var generation = (await link.RequestAsync("hoshidicts-offscreen", "hd_status"))?["generation"]?.GetValue<long>() ?? 0;
    var live = HachidoriPayloads.Media(await link.RequestAsync("hoshidicts-offscreen", "hd_media",
        new JsonObject { ["dictionary"] = dictionary, ["path"] = path, ["generation"] = generation }));
    var local = engine.Media(dictionary, path);
    compared++;
    if (live is null ? local is not null : local is null || !live.AsSpan().SequenceEqual(local))
    {
        differences++;
        Console.WriteLine($"DIFF hd_media {dictionary} {path}: live {live?.Length ?? 0} bytes, offline {local?.Length ?? 0} bytes");
    }
}

Console.WriteLine($"{compared} comparisons, {differences} differences. Time in requests: live {liveMs:0} ms (relay round trips), offline {localMs:0} ms.");
return differences == 0 ? 0 : 1;

// ------------------------------------------------------------------- helpers

static JsonObject Pick(JsonNode? reply, params string[] keys)
{
    var picked = new JsonObject();
    foreach (var key in keys) picked[key] = reply?[key]?.DeepClone();
    return picked;
}

void CollectImages(JsonNode? reply)
{
    foreach (var glossary in (reply?["results"] as JsonArray ?? []).SelectMany(r => r?["term"]?["glossaries"] as JsonArray ?? []))
    {
        var dictionary = glossary?["dictionary"]?.ToString() ?? string.Empty;
        var content = glossary?["glossary"]?.ToString() ?? string.Empty;
        if (!content.Contains("\"img\"")) continue;
        try { Walk(JsonNode.Parse(content)); }
        catch (JsonException) { }

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["tag"]?.ToString() == "img" && o["path"]?.ToString() is { Length: > 0 } path) images.Add((dictionary, path));
                    foreach (var (_, child) in o) Walk(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
    }
}

static (string Path, JsonNode? Left, JsonNode? Right) FirstDifference(JsonNode? left, JsonNode? right, string path)
{
    if (left is JsonObject lo && right is JsonObject ro)
    {
        foreach (var key in lo.Select(p => p.Key).Union(ro.Select(p => p.Key)))
        {
            if (!JsonNode.DeepEquals(lo[key], ro[key])) return FirstDifference(lo[key], ro[key], $"{path}.{key}");
        }
    }
    if (left is JsonArray la && right is JsonArray ra)
    {
        for (var i = 0; i < Math.Max(la.Count, ra.Count); i++)
        {
            var l = i < la.Count ? la[i] : null;
            var r = i < ra.Count ? ra[i] : null;
            if (!JsonNode.DeepEquals(l, r)) return FirstDifference(l, r, $"{path}[{i}]");
        }
    }
    return (path, left, right);
}

static string Clip(JsonNode? node)
{
    var text = node?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "(missing)";
    return text.Length > 300 ? text[..300] + "…" : text;
}
