using System.IO.Compression;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

internal static class NpcImageChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");
        var first = new NpcPicture("新しい名前", "あたらしいなまえ", png, 1, 1);
        var second = new NpcPicture("名前", "なまえ", png, 1, 1);
        var archive = NpcImageDictionary.Build([first, second]);
        using (var zip = new ZipArchive(new MemoryStream(archive)))
        {
            JsonNode Read(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return JsonNode.Parse(reader.ReadToEnd())!;
            }
            var index = Read("index.json");
            var rows = Read("term_bank_1.json").AsArray();
            check(index["title"]!.GetValue<string>() == "FFXIV NPC pictures" && index["format"]!.GetValue<int>() == 3
                  && rows.Count == 2 && rows.Any(r => r![0]!.GetValue<string>() == "新しい名前" && r[1]!.GetValue<string>() == "あたらしいなまえ"),
                "NPC picture dictionary holds every picture under one title");
            var image = GlossaryBuilder.Build(rows[0]![5], 0, false, index["title"]!.GetValue<string>()).Single().Image;
            using var bytes = new MemoryStream();
            zip.GetEntry(image!.Path)!.Open().CopyTo(bytes);
            check(bytes.ToArray().SequenceEqual(png) && zip.Entries.Count(e => e.FullName.EndsWith(".png")) == 1,
                "NPC picture dictionary is renderable and stores identical pictures once");
        }
        check(NpcImageDictionary.Revision([first, second]) == NpcImageDictionary.Revision([second, first])
              && NpcImageDictionary.Revision([first]) != NpcImageDictionary.Revision([first with { Reading = "べつ" }]),
            "NPC picture revision is order-independent and distinguishes readings");
        check(NpcImageDictionary.Fit(1920, 1080) == (480, 270) && NpcImageDictionary.Fit(100, 50) == (100, 50)
              && NpcImageDictionary.PngSize(png) == (1, 1), "NPC pictures are capped at 480px and PNG sizes are read");

        var requests = new List<(string Type, JsonObject Payload)>();
        var uploaded = new MemoryStream();
        Task<JsonNode?> Send(string type, JsonObject payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requests.Add((type, payload));
            if (type == "hd_import_begin") return Task.FromResult<JsonNode?>(new JsonObject { ["token"] = "test", ["chunkBytes"] = 13 });
            if (type == "hd_import_chunk")
            {
                if (payload["offset"]!.GetValue<int>() != uploaded.Length) throw new Exception("Chunks arrived out of order.");
                uploaded.Write(Convert.FromBase64String(payload["data"]!.GetValue<string>()));
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        }
        await NpcImageDictionary.UploadAsync(archive, Send, default);
        check(uploaded.ToArray().SequenceEqual(archive) && requests[0].Payload["replace"]!.GetValue<bool>()
              && requests.Last().Type == "hd_import_commit", "NPC upload sends ordered chunks and commits a replaceable host dictionary");

        requests.Clear();
        async Task<JsonNode?> FailChunk(string type, JsonObject payload, CancellationToken ct)
        {
            if (type == "hd_import_chunk") throw new IOException("Chunk failed.");
            return await Send(type, payload, ct);
        }
        try { await NpcImageDictionary.UploadAsync(archive, FailChunk, default); throw new Exception("Failure was ignored."); }
        catch (IOException) { }
        check(requests.Last().Type == "hd_import_abort" && requests.All(r => r.Type != "hd_import_commit"),
            "Failed NPC uploads abort without changing the installed dictionary");

        requests.Clear();
        uploaded.SetLength(0);
        async Task<JsonNode?> UncertainCommit(string type, JsonObject payload, CancellationToken ct)
        {
            var result = await Send(type, payload, ct);
            if (type == "hd_import_commit") throw new IOException("Connection lost after commit.");
            return result;
        }
        try { await NpcImageDictionary.UploadAsync(archive, UncertainCommit, default); throw new Exception("Failure was ignored."); }
        catch (IOException) { }
        check(requests.Count(r => r.Type == "hd_import_commit") == 1 && requests.All(r => r.Type != "hd_import_abort"),
            "Ambiguous NPC commits are never retried automatically");

        await LibraryChecks(check, png);
    }

    private sealed class FakeHost : INpcPictureHost
    {
        public readonly List<HostDictionary> Installed = [];
        public readonly Dictionary<string, byte[]> Media = [];
        public readonly List<string> Removed = [];
        public int Commits;
        public bool LoseCommitReply;
        public byte[]? LastArchive;
        private readonly MemoryStream upload = new();
        public IReadOnlyList<HostDictionary> Dictionaries => Installed.ToList();
        public Task<long> GenerationAsync(CancellationToken ct) => Task.FromResult(7L);
        public Task<byte[]?> MediaAsync(string dictionary, string path, long generation, CancellationToken ct) =>
            Task.FromResult(generation == 7 && path == NpcImageDictionary.LegacyImagePath ? Media.GetValueOrDefault(dictionary) : null);
        public Task RemoveAsync(HostDictionary dictionary, CancellationToken ct)
        {
            Removed.Add(dictionary.Title);
            Installed.RemoveAll(d => d.Id == dictionary.Id);
            return Task.CompletedTask;
        }
        public Task<JsonNode?> ImportAsync(string type, JsonObject payload, CancellationToken ct)
        {
            if (type == "hd_import_begin")
            {
                upload.SetLength(0);
                return Task.FromResult<JsonNode?>(new JsonObject { ["token"] = "t", ["chunkBytes"] = 4096 });
            }
            if (type == "hd_import_chunk") upload.Write(Convert.FromBase64String(payload["data"]!.GetValue<string>()));
            if (type == "hd_import_commit")
            {
                Commits++;
                LastArchive = upload.ToArray();
                using var zip = new ZipArchive(new MemoryStream(LastArchive));
                using var reader = new StreamReader(zip.GetEntry("index.json")!.Open());
                var index = JsonNode.Parse(reader.ReadToEnd())!;
                var title = index["title"]!.GetValue<string>();
                Installed.RemoveAll(d => d.Title == title);
                Installed.Add(new HostDictionary("combined-" + Commits, title, index["revision"]!.GetValue<string>()));
                if (LoseCommitReply) throw new IOException("Reply lost.");
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        }
        public int Rows()
        {
            using var zip = new ZipArchive(new MemoryStream(LastArchive!));
            return zip.Entries.Where(e => e.Name.StartsWith("term_bank_")).Sum(e =>
            {
                using var reader = new StreamReader(e.Open());
                return JsonNode.Parse(reader.ReadToEnd())!.AsArray().Count;
            });
        }
    }

    private static async Task LibraryChecks(Action<bool, string> check, byte[] png)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hachidori-npc-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new FakeHost();
            foreach (var (id, title) in new[]
            {
                ("legacy-alphinaud", """FFXIV picture · ["アルフィノ","あるふぃの"]"""),
                ("legacy-alisaie", """FFXIV picture · ["アリゼー","あ,り\"ぜー"]"""),
            })
            {
                host.Installed.Add(new HostDictionary(id, title, "x"));
                host.Media[title] = png;
            }
            host.Installed.Add(new HostDictionary("legacy-missing", """FFXIV picture · ["消えた","きえた"]""", "x"));
            host.Installed.Add(new HostDictionary("legacy-malformed", "FFXIV picture · nope", "x"));
            host.Installed.Add(new HostDictionary("jitendex", "Jitendex", "y"));
            var library = new NpcPictureLibrary(directory, host);

            var result = await library.AddAsync(new NpcPicture("ヤ・シュトラ", "や・しゅとら", png, 1, 1), false, default);
            check(result == new NpcSaveResult(3, 2, 1) && host.Commits == 1 && host.Rows() == 3
                  && host.Installed.Count(d => d.Title == NpcImageDictionary.Title) == 1
                  && host.Removed.Count == 2 && host.Installed.Any(d => d.Id == "legacy-missing")
                  && host.Installed.Any(d => d.Id == "legacy-malformed") && host.Installed.Any(d => d.Title == "Jitendex"),
                "A save merges old one-picture dictionaries into one and removes only merged ones");
            check(library.List().Count == 3 && library.Contains("アリゼー", "あ,り\"ぜー"), "Merged pictures keep their original readings on this computer");

            await library.AddAsync(new NpcPicture("ヤ・シュトラ", "や・しゅとら", png, 1, 1), false, default);
            check(host.Commits == 2 && host.Rows() == 3, "Recapturing a picture replaces it instead of adding a dictionary");

            host.LoseCommitReply = true;
            try { await library.AddAsync(new NpcPicture("名前", "なまえ", png, 1, 1), false, default); throw new Exception("Lost reply was ignored."); }
            catch (IOException) { }
            host.LoseCommitReply = false;
            await library.RemoveAsync("名前", "なまえ", false, default);
            check(host.Rows() == 3, "A dictionary committed with a lost reply is still recognised as this computer's");

            host.Installed.RemoveAll(d => d.Title == NpcImageDictionary.Title);
            host.Installed.Add(new HostDictionary("foreign", NpcImageDictionary.Title, "made-elsewhere"));
            try { await library.RemoveAsync("アリゼー", "あ,り\"ぜー", false, default); throw new Exception("Conflict was ignored."); }
            catch (NpcPictureConflictException) { }
            check(host.Installed.Any(d => d.Id == "foreign") && library.Contains("アリゼー", "あ,り\"ぜー"),
                "A picture dictionary made elsewhere is never replaced without confirmation");
            await library.RemoveAsync("アリゼー", "あ,り\"ぜー", true, default);
            check(host.Installed.All(d => d.Id != "foreign") && host.Rows() == 2 && !library.Contains("アリゼー", "あ,り\"ぜー"),
                "Confirmed replacement removes one picture");

            await library.RemoveAsync("アルフィノ", "あるふぃの", false, default);
            await library.RemoveAsync("ヤ・シュトラ", "や・しゅとら", false, default);
            check(library.List().Count == 0 && host.Installed.All(d => d.Title != NpcImageDictionary.Title)
                  && Directory.GetFiles(directory, "*.png").Length == 0,
                "Removing the last picture removes the host dictionary and its files");

            File.WriteAllText(Path.Combine(directory, "index.json"), "{broken");
            var commits = host.Commits;
            try { await library.AddAsync(new NpcPicture("名前", "なまえ", png, 1, 1), true, default); throw new Exception("Corruption was ignored."); }
            catch (HachidoriException) { }
            check(host.Commits == commits, "An unreadable picture list blocks saving instead of wiping the host");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
