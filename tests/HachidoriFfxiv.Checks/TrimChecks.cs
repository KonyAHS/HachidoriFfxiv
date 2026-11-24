using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using HachidoriFfxiv.Rendering.Css;
using NAudio.CoreAudioApi;

/// <summary>
/// Checks the Release output as it ships: the audio libraries trimmed by the build, and the theme
/// sheets embedded minified and deflated. The plugin's own audio code runs against the trimmed
/// libraries in a separate load context, so nothing here falls back to the full packages.
/// </summary>
internal static class TrimChecks
{
    private static readonly string[] Trimmed = ["Concentus", "Concentus.Oggfile", "NAudio.Core", "NAudio.Vorbis", "NAudio.Wasapi", "NVorbis"];

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = RepositoryRoot();
        var output = Path.Combine(root, "HachidoriFfxiv", "bin", "x64", "Release");
        if (!File.Exists(Path.Combine(output, "HachidoriFfxiv.dll")))
        {
            Console.WriteLine("SKIP: shipped-output checks (no Release build of the plugin)");
            return;
        }

        foreach (var name in Trimmed)
        {
            var full = new FileInfo(Path.Combine(AppContext.BaseDirectory, name + ".dll")).Length;
            var shipped = new FileInfo(Path.Combine(output, name + ".dll")).Length;
            check(shipped < full, $"{name} ships trimmed ({shipped / 1024} KB of {full / 1024} KB)");
        }

        // A COM interface is a vtable: a trimmed method shifts every later slot.
        var broken = new List<string>();
        var interfaceCount = 0;
        foreach (var name in new[] { "NAudio.Core", "NAudio.Wasapi" })
        {
            var full = ComInterfaces(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
            var shipped = ComInterfaces(Path.Combine(output, name + ".dll"));
            interfaceCount += shipped.Count;
            broken.AddRange(shipped.Where(i => full[i.Key] != i.Value).Select(i => $"{i.Key} {i.Value}/{full[i.Key]}"));
        }
        check(interfaceCount > 0 && broken.Count == 0,
            $"trimmed NAudio keeps the vtable methods of its {interfaceCount} COM interfaces{(broken.Count > 0 ? ": " + string.Join(", ", broken) : "")}");

        var context = new ShippedContext(output);
        try
        {
            var plugin = context.LoadFromAssemblyPath(Path.Combine(output, "HachidoriFfxiv.dll"));
            CheckThemeSheets(check, plugin, root);
            CheckFrames(check, plugin);
            await CheckAudioAsync(check, plugin);
        }
        finally
        {
            context.Unload();
        }
    }

    private static void CheckThemeSheets(Action<bool, string> check, Assembly plugin, string root)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var themes = Path.Combine(root, "HachidoriFfxiv", "Themes");
        var palettes = Regex.Matches(File.ReadAllText(Path.Combine(themes, "default.css")), @":host\(\[data-hoshidicts-theme=""([a-z0-9-]+)""\]\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        long source = 0, embedded = 0;
        var cases = 0;
        var different = new List<string>();
        foreach (var renderer in (string[])["default", "nazeka", "plain", "jl", "bee", "crystal"])
        {
            var css = File.ReadAllText(Path.Combine(themes, renderer + ".css"));
            using var resource = plugin.GetManifestResourceStream($"HachidoriFfxiv.Themes.{renderer}.css.deflate");
            if (resource is null)
            {
                different.Add($"{renderer} (missing)");
                continue;
            }

            source += new FileInfo(Path.Combine(themes, renderer + ".css")).Length;
            embedded += resource.Length;
            using var reader = new StreamReader(new DeflateStream(resource, CompressionMode.Decompress));
            var packed = reader.ReadToEnd();
            foreach (var palette in renderer == "default" ? palettes : [renderer])
            {
                foreach (var variant in (string[])["auto", "grey", "beige", "blue"])
                {
                    cases++;
                    var expected = JsonSerializer.Serialize(PopupThemeEngine.Compute(new(renderer, palette, variant, css, "", 85)), options);
                    var actual = JsonSerializer.Serialize(PopupThemeEngine.Compute(new(renderer, palette, variant, packed, "", 85)), options);
                    if (expected != actual)
                        different.Add($"{renderer}/{palette}/{variant}");
                }
            }
        }

        check(different.Count == 0,
            $"embedded theme sheets ({embedded / 1024} KB of {source / 1024} KB) give the same {cases} popup themes{(different.Count > 0 ? ": " + string.Join(", ", different.Take(5)) : "")}");
    }

    private static void CheckFrames(Action<bool, string> check, Assembly plugin)
    {
        // A padded BGRA readback (pitch wider than the image) is packed into RGBA in its own array.
        const int width = 5, height = 3, pitch = 32;
        var data = new byte[pitch * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * pitch + x * 4;
                data[i] = (byte)(10 * x + y);        // B
                data[i + 1] = (byte)(100 + x);       // G
                data[i + 2] = (byte)(200 + y);       // R
                data[i + 3] = 7;
            }

        var toRgba = plugin.GetType("HachidoriFfxiv.Media.GameFrames")!.GetMethod("ToRgba", BindingFlags.Static | BindingFlags.NonPublic)!;
        var frame = toRgba.Invoke(null, [width, height, pitch, 87, data])!;
        var rgba = (byte[])frame.GetType().GetProperty("Rgba")!.GetValue(frame)!;
        var correct = true;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                correct &= rgba[i] == 200 + y && rgba[i + 1] == 100 + x && rgba[i + 2] == 10 * x + y && rgba[i + 3] == 255;
            }

        check(correct, "GIF frames contain tightly packed RGBA pixels from padded BGRA readbacks");
    }

    private static async Task CheckAudioAsync(Action<bool, string> check, Assembly plugin)
    {
        var decoder = plugin.GetType("HachidoriFfxiv.Audio.PronunciationDecoder")!;
        var clips = plugin.GetType("HachidoriFfxiv.Media.AudioClips")!;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        const int rate = 48000;
        var tone = Enumerable.Range(0, rate / 2).Select(i => 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / rate)).ToArray();

        var opus = (byte[])clips.GetMethod("EncodeOpus")!.Invoke(null, [tone, rate, 48000])!;
        var sources = new (string Name, byte[] Data)[]
        {
            ("Ogg Opus (Concentus)", opus),
            ("Ogg Vorbis (NVorbis)", File.ReadAllBytes(Path.Combine(fixtures, "tone.ogg"))),
            ("MP3 (Media Foundation)", File.ReadAllBytes(Path.Combine(fixtures, "tone.mp3"))),
            ("WAV", Wav(tone, rate, 2)),
        };
        foreach (var (name, data) in sources)
        {
            var peak = 0f;
            try
            {
                using var stream = (Stream)decoder.GetMethod("Open")!.Invoke(null, [data])!;
                var buffer = new byte[stream.Length > 0 ? Math.Min(stream.Length, 1 << 20) : 1 << 16];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    peak = Math.Max(peak, Peak(buffer.AsSpan(0, read), stream));
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine($"  {name}: {ex.InnerException}");
            }

            check(peak > 0.1f, $"trimmed libraries decode {name} pronunciation audio (peak {peak:0.00})");
        }

        var decodeWav = clips.GetMethod("DecodeWav")!.Invoke(null, [Wav(tone, 24000, 2)])!;
        var samples = (float[])decodeWav.GetType().GetField("Item1")!.GetValue(decodeWav)!;
        check(samples.Length == tone.Length && samples.Max() > 0.25f, "trimmed libraries read stereo TTS WAV as mono samples");

        // OST Ogg Vorbis seek + slice extraction and TTS + OST mixing through the trimmed NVorbis/Concentus libraries.
        var extractor = plugin.GetType("HachidoriFfxiv.Media.ScdOstExtractor")!;
        var oggFixture = File.ReadAllBytes(Path.Combine(fixtures, "tone.ogg"));
        var track = extractor.GetMethod("FromOggData")!.Invoke(null, ["music/test/BGM_Test.scd", oggFixture, 0L, 0L])!;
        var ostSlice = (float[]?)extractor.GetMethod("ExtractSlice")!.Invoke(null, [track, 0.1, rate, rate]);
        check(ostSlice is { Length: rate } && ostSlice.Max(Math.Abs) > 0.05f,
            "trimmed libraries seek and decode Ogg Vorbis OST slices via NVorbis");
        var mixResult = clips.GetMethod("MixTtsWithOst")!.Invoke(null, [samples, 24000, ostSlice!, 0.28f, rate, 0.2f, 0.2f])!;
        var mixedSamples = (float[])mixResult.GetType().GetField("Item1")!.GetValue(mixResult)!;
        var mixedFlag = (bool)mixResult.GetType().GetField("Item2")!.GetValue(mixResult)!;
        var mixedOpus = (byte[])clips.GetMethod("EncodeOpus")!.Invoke(null, [mixedSamples, rate, 64000])!;
        check(mixedFlag && mixedSamples.Max(Math.Abs) <= 0.90f && mixedOpus.AsSpan(0, 4).SequenceEqual("OggS"u8),
            "trimmed libraries mix TTS with OST and encode to Ogg Opus");

        using (var devices = new MMDeviceEnumerator())
        {
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                Console.WriteLine("SKIP: pronunciation playback through the trimmed libraries (no sound device)");
                return;
            }
        }

        // The plugin's own player at zero volume: WasapiOut and the MP3 reader's COM calls, trimmed.
        var configuration = Activator.CreateInstance(plugin.GetType("HachidoriFfxiv.Configuration")!)!;
        configuration.GetType().GetProperty("AudioVolume")!.SetValue(configuration, 0f);
        var player = (IDisposable)Activator.CreateInstance(plugin.GetType("HachidoriFfxiv.Audio.AudioPlayer")!, configuration)!;
        using (player)
        {
            Exception? failure = null;
            try { player.GetType().GetMethod("Play")!.Invoke(player, [sources[2].Data]); }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
            await Task.Delay(600);
            var error = (string?)player.GetType().GetProperty("LastError")!.GetValue(player);
            check(failure is null && error is null, $"trimmed libraries play MP3 pronunciation through WASAPI ({failure?.Message ?? error ?? "ok"})");
        }
    }

    private static float Peak(ReadOnlySpan<byte> pcm, Stream stream)
    {
        var format = stream.GetType().GetProperty("WaveFormat")!.GetValue(stream)!;
        var bits = (int)format.GetType().GetProperty("BitsPerSample")!.GetValue(format)!;
        var peak = 0f;
        if (bits == 32)
            foreach (var v in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm)) peak = Math.Max(peak, Math.Abs(v));
        else
            foreach (var v in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm)) peak = Math.Max(peak, Math.Abs(v / 32768f));
        return peak;
    }

    private static byte[] Wav(float[] mono, int rate, int channels)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var bytes = mono.Length * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        foreach (var s in mono)
            for (var c = 0; c < channels; c++)
                w.Write((short)(s * 32767));
        w.Flush();
        return stream.ToArray();
    }

    private static Dictionary<string, int> ComInterfaces(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var result = new Dictionary<string, int>();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.Import) == 0)
                continue;
            var name = md.GetString(type.Name);
            var outer = type.GetDeclaringType();
            var ns = outer.IsNil ? md.GetString(type.Namespace) : md.GetString(md.GetTypeDefinition(outer).Namespace) + "." + md.GetString(md.GetTypeDefinition(outer).Name) + "/";
            result[ns.EndsWith('/') ? ns + name : ns + "." + name] = type.GetMethods().Count;
        }

        return result;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "HachidoriFfxiv", "Themes")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>Loads the plugin's output folder first, then Dalamud's assemblies, never this process's copies.</summary>
    private sealed class ShippedContext(string output) : AssemblyLoadContext("shipped plugin", isCollectible: true)
    {
        private readonly string dalamud = Environment.GetEnvironmentVariable("DALAMUD_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");

        protected override Assembly? Load(AssemblyName name)
        {
            foreach (var dir in new[] { output, dalamud })
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path))
                    return LoadFromAssemblyPath(path);
            }

            return null;
        }
    }
}
