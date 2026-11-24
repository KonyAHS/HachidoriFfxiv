using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>One personal picture for an exact headword and reading.</summary>
public sealed record NpcPicture(string Expression, string Reading, byte[] Png, int Width, int Height)
{
    public string Hash { get; } = Convert.ToHexStringLower(SHA256.HashData(Png));
    public bool Matches(string expression, string reading) =>
        string.Equals(Expression, expression, StringComparison.Ordinal) && string.Equals(Reading, reading, StringComparison.Ordinal);
}

/// <summary>
/// Every personal NPC picture packaged as a single host dictionary. Hachidori
/// cannot edit an installed dictionary in place, so each change rebuilds the
/// whole package and replaces it by title.
/// </summary>
public static class NpcImageDictionary
{
    public const string Capability = "linked-import-v1";
    public const string Target = "hachidori-linked-import";
    public const string Title = "FFXIV NPC pictures";
    /// <summary>Earlier releases installed one dictionary per picture under this prefix.</summary>
    public const string LegacyTitlePrefix = "FFXIV picture · ";
    public const string LegacyImagePath = "npc.png";
    /// <summary>Longest stored side. Every save re-uploads every picture, so they stay small.</summary>
    public const int MaxSide = 480;
    private const int RowsPerBank = 2000;

    public static bool IsPictureDictionary(string title) =>
        title == Title || title.StartsWith(LegacyTitlePrefix, StringComparison.Ordinal);

    /// <summary>The headword and reading a legacy per-picture dictionary title names.</summary>
    public static bool TryParseLegacyTitle(string title, out string expression, out string reading)
    {
        expression = reading = "";
        if (!title.StartsWith(LegacyTitlePrefix, StringComparison.Ordinal)) return false;
        try
        {
            if (JsonNode.Parse(title[LegacyTitlePrefix.Length..]) is not JsonArray { Count: 2 } tuple
                || tuple[0] is not JsonValue e || !e.TryGetValue(out string? exp) || string.IsNullOrWhiteSpace(exp)
                || tuple[1] is not JsonValue r || !r.TryGetValue(out string? read)) return false;
            (expression, reading) = (exp, read);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The size a picture is stored at: at most <see cref="MaxSide"/> on its long side.</summary>
    public static (int Width, int Height) Fit(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("A picture needs positive dimensions.");
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    /// <summary>Width and height from a PNG's IHDR chunk.</summary>
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new ArgumentException("The picture must be PNG.");
        return (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]));
    }

    private static IEnumerable<NpcPicture> Ordered(IEnumerable<NpcPicture> pictures) =>
        pictures.OrderBy(p => p.Expression, StringComparer.Ordinal).ThenBy(p => p.Reading, StringComparer.Ordinal);

    /// <summary>Identifies a picture set; the same set always builds the same revision.</summary>
    public static string Revision(IEnumerable<NpcPicture> pictures)
    {
        var text = new StringBuilder();
        foreach (var p in Ordered(pictures))
            text.Append(p.Expression).Append('\0').Append(p.Reading).Append('\0').Append(p.Hash)
                .Append('\0').Append(p.Width).Append('\0').Append(p.Height).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static byte[] Build(IReadOnlyCollection<NpcPicture> pictures)
    {
        if (pictures.Count == 0) throw new ArgumentException("The picture dictionary needs at least one picture.");
        foreach (var p in pictures)
        {
            if (string.IsNullOrWhiteSpace(p.Expression) || p.Width <= 0 || p.Height <= 0)
                throw new ArgumentException("A picture needs a headword and positive dimensions.");
            if (!IsPng(p.Png)) throw new ArgumentException("The picture must be PNG.");
        }
        var ordered = Ordered(pictures).ToList();
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
                ["title"] = Title, ["format"] = 3, ["revision"] = Revision(ordered),
                ["sequenced"] = false, ["author"] = "Hachidori for FFXIV",
                ["description"] = $"Your {ordered.Count} personal FFXIV screenshot(s). Managed by Hachidori for FFXIV: "
                    + "capturing or removing a picture in game replaces this dictionary.",
            });
            for (var start = 0; start < ordered.Count; start += RowsPerBank)
            {
                var rows = new JsonArray();
                foreach (var p in ordered.Skip(start).Take(RowsPerBank))
                    rows.Add(new JsonArray(p.Expression, p.Reading, "", "", 0,
                        new JsonArray(new JsonObject
                        {
                            ["type"] = "image", ["path"] = ImagePath(p), ["width"] = p.Width, ["height"] = p.Height,
                            ["title"] = p.Expression, ["description"] = "Personal FFXIV screenshot · SQUARE ENIX",
                        }), 0, ""));
                Json($"term_bank_{start / RowsPerBank + 1}.json", rows);
            }
            foreach (var p in ordered.DistinctBy(p => p.Hash))
            {
                using var image = zip.CreateEntry(ImagePath(p), CompressionLevel.NoCompression).Open();
                image.Write(p.Png);
            }
        }
        return bytes.ToArray();
    }

    private static string ImagePath(NpcPicture picture) => $"npc/{picture.Hash}.png";

    public static async Task UploadAsync(byte[] archive,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> send, CancellationToken ct)
    {
        var begin = await send("hd_import_begin", new JsonObject
        {
            ["fileName"] = "hachidori-ffxiv-npc-pictures.zip", ["size"] = archive.Length, ["replace"] = true,
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
