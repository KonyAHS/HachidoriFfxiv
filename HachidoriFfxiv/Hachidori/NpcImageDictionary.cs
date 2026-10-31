using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>One replaceable image package per exact headword/reading, stored on the host.</summary>
public static class NpcImageDictionary
{
    public const string Capability = "linked-import-v1";
    public const string Target = "hachidori-linked-import";

    public static string Title(string expression, string reading)
    {
        // A quoted tuple avoids delimiter collisions while keeping the host's
        // dictionary list readable. Identity is independent of the picture.
        var identity = new JsonArray(expression, reading).ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return "FFXIV picture · " + identity;
    }

    public static byte[] Build(string expression, string reading, byte[] png, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(expression) || width <= 0 || height <= 0)
            throw new ArgumentException("An image needs a headword and positive dimensions.");
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("The captured image must be PNG.");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Json(string path, JsonNode value)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(value.ToJsonString());
            }
            Json("index.json", new JsonObject
            {
                ["title"] = Title(expression, reading), ["format"] = 3,
                ["revision"] = Convert.ToHexStringLower(SHA256.HashData(png)),
                ["sequenced"] = false, ["author"] = "Hachidori for FFXIV",
                ["description"] = $"Personal screenshot for {expression}. Replaced when you capture another picture of this headword and reading.",
            });
            Json("term_bank_1.json", new JsonArray(new JsonArray(
                expression, reading, "", "", 0,
                new JsonArray(new JsonObject
                {
                    ["type"] = "image", ["path"] = "npc.png", ["width"] = width, ["height"] = height,
                    ["title"] = expression, ["description"] = "Personal FFXIV screenshot · SQUARE ENIX",
                }), 0, "")));
            using var image = zip.CreateEntry("npc.png", CompressionLevel.NoCompression).Open();
            image.Write(png);
        }
        return bytes.ToArray();
    }

    public static async Task UploadAsync(byte[] archive,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> send, CancellationToken ct)
    {
        var begin = await send("hd_import_begin", new JsonObject
        {
            ["fileName"] = "hachidori-ffxiv-npc-image.zip", ["size"] = archive.Length, ["replace"] = true,
        }, ct).ConfigureAwait(false);
        var token = begin?["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token)) throw new HachidoriException("The host returned no dictionary upload token.");
        var committing = false;
        try
        {
            var chunkBytes = begin?["chunkBytes"]?.GetValue<int>() ?? 0;
            if (chunkBytes <= 0) throw new HachidoriException("The host returned an invalid upload chunk size.");
            chunkBytes = Math.Min(chunkBytes, 1024 * 1024);
            for (var offset = 0; offset < archive.Length; offset += chunkBytes)
                await send("hd_import_chunk", new JsonObject
                {
                    ["token"] = token, ["offset"] = offset,
                    ["data"] = Convert.ToBase64String(archive, offset, Math.Min(chunkBytes, archive.Length - offset)),
                }, ct).ConfigureAwait(false);
            // Never automatically retry an ambiguous commit: the host may have saved it.
            committing = true;
            var reply = await send("hd_import_commit", new JsonObject { ["token"] = token }, ct).ConfigureAwait(false);
            if (reply?["ok"]?.GetValue<bool>() != true)
                throw new HachidoriException(reply?["error"]?.ToString() ?? "The host did not confirm the image import.");
        }
        catch
        {
            if (!committing)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await send("hd_import_abort", new JsonObject { ["token"] = token }, cleanup.Token).ConfigureAwait(false);
                }
                catch { /* Uploads also expire on the host. */ }
            }
            throw;
        }
    }
}
