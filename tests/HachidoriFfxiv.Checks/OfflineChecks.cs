using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Offline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Offline mode without a Hachidori host: the native engine over the repo's own
/// FFXIV dictionary (imported with the DLL's hdw_import), the furigana port
/// against Hachidori's own output, and dictionary sync against a fake relay.
/// Live parity is tests/HachidoriFfxiv.Parity.
/// </summary>
internal static class OfflineChecks
{
    private const string DictionaryTitle = "FFXIV Hachidori JP–EN";

    public static async Task Run(Action<bool, string> check)
    {
        CheckFurigana(check);
        CheckPlan(check);
        check(OfflinePlan.IsNewer("0.6.0", "0.5.2") && !OfflinePlan.IsNewer("0.5.2", "0.5.2") && !OfflinePlan.IsNewer("", "0.5.2"),
            "offline engine version warnings compare Hachidori versions");
        check(OfflineAudio.SourceUrl("http://127.0.0.1:5050/?term={term}&reading={reading}&x={other}", "日本(!)", "にほん")
              == "http://127.0.0.1:5050/?term=%E6%97%A5%E6%9C%AC%28%21%29&reading=%E3%81%AB%E3%81%BB%E3%82%93&x=%7Bother%7D",
            "offline audio URLs encode terms as Hachidori's audioSourceUrl does");

        var root = Path.Combine(Path.GetTempPath(), "hachidori-ffxiv-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var native = NativeEngine.LoadFrom(AppContext.BaseDirectory);
            check(native.Version.Hachidori.Length > 0 && native.Version.DictionaryFormat >= 6, "hdw_native reports what it was built from");
            var imported = native.Import(FindDictionaryArchive(), Path.Combine(root, "imported"));
            var importedOk = imported["success"]?.GetValue<bool>() == true;
            check(importedOk, "the native engine imports the FFXIV dictionary" + (importedOk ? "" : $": {imported["error"]}"));
            var package = Path.Combine(root, "imported", DictionaryTitle);

            CheckEngine(check, native, package);
            await CheckSync(check, native, package, Path.Combine(root, "store"));
        }
        finally
        {
            OfflineStore.TryDelete(root);
        }
    }

    private static void CheckFurigana(Action<bool, string> check)
    {
        // Expected splits from Hachidori 0.5.2's HDGlossary.kanjiFurigana (render/glossary.js)
        // with its bundled KANJIDIC and kanwa tables; null means "the renderer's own split".
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kanji-furigana.json")))!.AsArray();
        var failures = new List<string>();
        foreach (var item in cases.OfType<JsonArray>())
        {
            var (expression, reading) = (item[0]!.GetValue<string>(), item[1]!.GetValue<string>());
            var actual = KanjiFurigana.Shared.For(expression, reading);
            var expected = item[2] as JsonArray;
            var same = expected is null
                ? actual is null
                : actual is not null && actual.Count == expected.Count
                  && actual.Zip(expected).All(p => p.First.Text == p.Second!["text"]!.GetValue<string>() && p.First.Reading == p.Second["reading"]!.GetValue<string>());
            if (!same) failures.Add($"{expression}【{reading}】");
        }
        check(failures.Count == 0, $"offline furigana matches Hachidori's kanjiFurigana ({cases.Count} words){(failures.Count > 0 ? ": " + string.Join(", ", failures) : "")}");
    }

    private static void CheckPlan(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "hachidori-ffxiv-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OfflineStore(root);
            OfflineDictionary Entry(string id, long terms = 1, long freq = 0) =>
                new() { Id = id, Title = id.ToUpperInvariant(), Revision = "1", Folder = id, TermCount = terms, FrequencyCount = freq };
            store.SaveManifest(new OfflineManifest { Dictionaries = [Entry("a"), Entry("b", 0, 5), Entry("c")] });
            var v1 = JsonNode.Parse("""{"dictionaryState":{"schemaVersion":1,"dictionaries":[{"id":"c"},{"id":"a","enabled":false},{"id":"b"},{"id":"gone"}]}}""")!.AsObject();
            var plan = OfflinePlan.For(store, v1);
            check(string.Join(",", plan.Select(d => $"{d.Id}{(d.Enabled ? "" : "-")}")) == "c,a-,b"
                  && plan.Single(d => d.Id == "b").Kinds.SequenceEqual([NativeEngine.Kind.Frequency]),
                "offline load plan follows Hachidori's dictionary order, enabled flags and kinds");
            var v2 = JsonNode.Parse("""{"dictionaryState":{"schemaVersion":2,"dictionaries":[{"id":"a"},{"id":"b"},{"id":"c"}],"profiles":{"ja":{"dictionaries":[{"id":"b"},{"id":"a","enabled":false}]}}}}""")!.AsObject();
            check(string.Join(",", OfflinePlan.For(store, v2).Select(d => $"{d.Id}{(d.Enabled ? "" : "-")}")) == "b,a-",
                "offline load plan uses the Japanese profile once dictionaries have profiles");
            check(new OfflineStore(root).Manifest.Dictionaries.Count == 3 && !File.Exists(Path.Combine(root, "manifest.json.tmp")),
                "the offline manifest is replaced atomically and reads back");
        }
        finally
        {
            OfflineStore.TryDelete(root);
        }
    }

    private static void CheckEngine(Action<bool, string> check, NativeEngine native, string package)
    {
        var engine = new LocalEngine(native, () => KanjiFurigana.Shared);
        var dictionary = new LocalDictionary("ffxiv", DictionaryTitle, package, true, 1, 0, 0, 0);
        engine.Load([dictionary, dictionary with { Id = "missing", Path = package + "-missing" }]);
        check(engine.Loaded.Count == 1 && engine.Failures.Single().Dictionary.Id == "missing" && engine.DictionaryCount == 1,
            "a package that does not load is reported and the others still load");
        var generation = engine.Generation;

        var options = HachidoriPayloads.LookupOptions(true, new JsonObject());
        var reply = engine.Lookup(HachidoriPayloads.Lookup("アルフィノと話す", 16, 16, options));
        var parsed = HachidoriPayloads.LookupReply(reply, "アルフィノと話す");
        var first = parsed.Results.FirstOrDefault();
        check(first?.Term.Expression == "アルフィノ" && first.Matched == "アルフィノ" && parsed.Generation == generation,
            "offline lookup answers from the local engine in the live reply shape");
        var tag = first?.Term.Glossaries.First().Tags.SingleOrDefault();
        check(tag is { Name: "character", Category: "default", Notes: "Character / NPC" },
            "offline glossaries carry definition tags expanded from the tag bank");

        var limited = engine.Lookup(HachidoriPayloads.Lookup("リムサ・ロミンサ", 1, 16, options));
        check(limited["results"]!.AsArray().Count == 1, "offline lookups honour maxResults from a constructed request");

        var scions = HachidoriPayloads.LookupReply(engine.Lookup(HachidoriPayloads.Lookup("暁の血盟", 4, 16, options)), "暁の血盟");
        check(scions.Results.FirstOrDefault()?.Term.Furigana?.Select(s => s.Text + s.Reading).SequenceEqual(["暁あかつき", "の", "血けつ", "盟めい"]) == true,
            "offline headwords carry Hachidori's KANJIDIC furigana");

        var personalOff = HachidoriPayloads.LookupOptions(false, new JsonObject { ["frequencyOrder"] = "nonsense" });
        check(engine.Lookup(HachidoriPayloads.Lookup("冒険者", 4, 16, personalOff))["results"]!.AsArray().Count > 0,
            "offline lookups accept the plugin's options without a personal dictionary");

        var line = "暁の血盟の冒険者アルフィノ";
        var spans = WordSpan.Parse(engine.Segment(HachidoriPayloads.Segment(line, 16, true)), line);
        check(WordSpan.At(spans, 1) == new WordSpan(0, 4) && WordSpan.At(spans, 9) == new WordSpan(8, 5),
            "offline segmentation splits a line into dictionary words");

        var image = engine.Media(DictionaryTitle, "images/alphinaud.png");
        check(image is { Length: > 8 } && image[0] == 0x89 && image[1] == (byte)'P', "offline dictionary images load from media.bin");
        check(engine.Media(DictionaryTitle, "images/none.png") is null, "a missing offline image is null, not an error");
        check(engine.Kanji(HachidoriPayloads.Kanji("暁"))["kanji"] is null, "offline kanji lookups without kanji dictionaries answer null");

        // A profile folder with non-ASCII characters (a Japanese Windows user name).
        var unicodeRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(package))!, "ユーザー名 テスト");
        var unicodePackage = Path.Combine(unicodeRoot, "ffxiv");
        Directory.CreateDirectory(unicodePackage);
        foreach (var file in Directory.EnumerateFiles(package)) File.Copy(file, Path.Combine(unicodePackage, Path.GetFileName(file)));
        engine.Load([dictionary with { Path = unicodePackage }]);
        check(engine.Failures.Count == 0 && engine.Lookup(HachidoriPayloads.Lookup("冒険者", 4, 16, options))["results"]!.AsArray().Count > 0,
            "dictionaries load from a folder with non-ASCII characters");

        engine.Unload();
        check(engine.Loaded.Count == 0 && engine.Generation > generation, "unloading releases the dictionaries and bumps the generation");
    }

    private static async Task CheckSync(Action<bool, string> check, NativeEngine native, string package, string storeRoot)
    {
        // A fake relay: GET /dictionaries and GET /dictionaries/<id>, serving
        // Hachidori backup archives built from the imported package.
        var archives = new Dictionary<string, byte[]>();
        var listing = new List<RemoteDictionary>();
        var downloads = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/dictionaries", () => Results.Json(new
        {
            dictionaries = listing.Select(d => new { id = d.Id, title = d.Title, revision = d.Revision, fileName = d.Title + ".zip" }),
        }));
        app.MapGet("/dictionaries/{id}", (string id) =>
        {
            downloads.Add(id);
            return archives.TryGetValue(id, out var bytes) ? Results.Bytes(bytes, "application/zip") : Results.NotFound();
        });
        await app.StartAsync();
        var api = new Uri(app.Urls.Single() + "/");

        archives["ffxiv"] = Backup(package, DictionaryTitle, marker: null);
        listing.Add(new RemoteDictionary("ffxiv", DictionaryTitle, "r1"));
        var store = new OfflineStore(storeRoot);
        using var http = new HttpClient();
        var sync = new OfflineSync(store, http, () => api, native.Version.DictionaryFormat);
        var snapshot = JsonNode.Parse("""{"dictionaryState":{"dictionaries":[{"id":"ffxiv","enabled":true}]},"options":{"popupTheme":"crystal"}}""")!.AsObject();

        var first = await sync.SyncAsync(snapshot, "0.5.2", native.Version.Hachidori, CancellationToken.None);
        var installed = store.Manifest.Dictionaries.SingleOrDefault();
        check(first.Downloaded == 1 && installed is { TermCount: > 0, Format: >= 5 } && Directory.Exists(store.PathOf(installed))
              && store.Snapshot?["options"]?["popupTheme"]?.ToString() == "crystal",
            "setup downloads each dictionary, records its revision and saves the settings snapshot");

        var again = await sync.SyncAsync(snapshot, "0.5.2", native.Version.Hachidori, CancellationToken.None);
        check(again is { Downloaded: 0, Kept: 1 } && downloads.Count == 1, "an unchanged revision is not downloaded again");

        listing[0] = listing[0] with { Revision = "r2" };
        var updated = await sync.SyncAsync(snapshot, "0.5.2", native.Version.Hachidori, CancellationToken.None);
        var replacement = store.Manifest.Dictionaries.Single();
        check(updated.Downloaded == 1 && replacement.Revision == "r2" && replacement.Folder != installed!.Folder
              && updated.Retired.Single() == store.PathOf(installed),
            "a changed revision downloads into a new folder and retires the old one");
        foreach (var folder in updated.Retired) OfflineStore.TryDelete(folder);

        archives["future"] = Backup(package, "Future", marker: ".hoshidicts_99");
        listing.Add(new RemoteDictionary("future", "Future", "f1"));
        await sync.SyncAsync(snapshot, "0.6.0", native.Version.Hachidori, CancellationToken.None);
        var skip = store.Manifest.Skipped.SingleOrDefault();
        check(skip is { Id: "future", Permanent: true } && skip.Reason.Contains("newer") && store.Manifest.Dictionaries.Count == 1,
            "a dictionary in a newer format is skipped with a reason, and the rest stay usable");
        var futureDownloads = downloads.Count(d => d == "future");
        await sync.SyncAsync(snapshot, "0.6.0", native.Version.Hachidori, CancellationToken.None);
        check(downloads.Count(d => d == "future") == futureDownloads, "an unreadable revision is not downloaded on every connection");

        listing.RemoveAll(d => d.Id == "ffxiv");
        var removed = await sync.SyncAsync(snapshot, "0.6.0", native.Version.Hachidori, CancellationToken.None);
        check(removed.Removed == 1 && store.Manifest.Dictionaries.Count == 0 && removed.Retired.Count == 1,
            "a dictionary removed from Hachidori is removed offline");
        foreach (var folder in removed.Retired) OfflineStore.TryDelete(folder);
        store.CleanUnreferenced();
        check(!Directory.EnumerateDirectories(store.DictionariesDirectory).Any() && !Directory.Exists(store.StagingDirectory),
            "cleanup leaves no unreferenced package or staging folders");

        var traversal = Path.Combine(storeRoot, "evil.zip");
        using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create))
        {
            zip.CreateEntry("dictionaries/0/.hoshidicts_6");
            zip.CreateEntry("dictionaries/0/../../escaped.txt");
        }
        try
        {
            OfflineSync.Extract(traversal, Path.Combine(storeRoot, "evil"), "Evil");
            check(false, "archive paths outside the package are rejected");
        }
        catch (OfflineEngineException ex) when (ex.Permanent)
        {
            check(!File.Exists(Path.Combine(storeRoot, "escaped.txt")), "archive paths outside the package are rejected");
        }
    }

    // A Hachidori backup archive (what the relay serves) holding one package.
    private static byte[] Backup(string package, string title, string? marker)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var row = new JsonObject { ["id"] = title, ["title"] = title, ["termCount"] = 29000, ["frequencyCount"] = 0, ["pitchCount"] = 0, ["kanjiCount"] = 0 };
            var index = new JsonObject
            {
                ["format"] = "hachidori-backup", ["version"] = 3,
                ["snapshot"] = new JsonObject { ["state"] = new JsonObject { ["dictionaries"] = new JsonArray(row) } },
            };
            using (var writer = new StreamWriter(zip.CreateEntry("hachidori-backup.json").Open(), Encoding.UTF8))
                writer.Write(index.ToJsonString());
            foreach (var file in Directory.EnumerateFiles(package))
            {
                var name = Path.GetFileName(file);
                if (marker is not null && name.StartsWith(".hoshidicts_", StringComparison.Ordinal)) name = marker;
                zip.CreateEntryFromFile(file, "dictionaries/0/" + name, CompressionLevel.NoCompression);
            }
        }
        return stream.ToArray();
    }

    private static string FindDictionaryArchive()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "dictionaries", "ffxiv", "FFXIV-Hachidori-JP-EN.zip");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("dictionaries/ffxiv/FFXIV-Hachidori-JP-EN.zip");
    }
}
