using System.IO.Compression;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

internal static class NpcImageChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");
        var archive = NpcImageDictionary.Build("新しい名前", "あたらしいなまえ", png, 1, 1);
        using (var zip = new ZipArchive(new MemoryStream(archive)))
        {
            JsonNode Read(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return JsonNode.Parse(reader.ReadToEnd())!;
            }
            var index = Read("index.json");
            var row = Read("term_bank_1.json")[0]!;
            check(row[0]!.GetValue<string>() == "新しい名前" && row[1]!.GetValue<string>() == "あたらしいなまえ"
                  && index["format"]!.GetValue<int>() == 3, "NPC image package matches the exact headword and reading");
            var image = GlossaryBuilder.Build(row[5], 0, false, index["title"]!.GetValue<string>()).Single().Image;
            using var bytes = new MemoryStream();
            zip.GetEntry(image!.Path)!.Open().CopyTo(bytes);
            check(bytes.ToArray().SequenceEqual(png), "NPC image package is renderable by the dictionary image path");
        }
        check(NpcImageDictionary.Title("名前", "なまえ") != NpcImageDictionary.Title("名前", "別の読み")
              && NpcImageDictionary.Title("名前", "なまえ") == NpcImageDictionary.Title("名前", "なまえ"),
            "NPC replacement identity preserves distinct readings");

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
    }
}
