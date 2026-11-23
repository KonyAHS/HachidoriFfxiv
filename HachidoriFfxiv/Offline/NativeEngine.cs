using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// hdw_native.dll: Hachidori's wasm/bindings.cpp over hoshidicts, built natively
/// (native/build.ps1). The engine is one global, non-reentrant instance and its
/// string results live in static buffers, so every call holds <see cref="Gate"/>.
/// </summary>
public sealed unsafe class NativeEngine : IDisposable
{
    public const string LibraryName = "hdw_native.dll";

    /// <summary>hdw_add_dict's <c>kind</c> argument.</summary>
    public enum Kind { Term = 0, Frequency = 1, Pitch = 2, Kanji = 3 }

    private readonly nint library;
    private readonly delegate* unmanaged[Cdecl]<byte*> lastError;
    private readonly delegate* unmanaged[Cdecl]<void> reset;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, int, int, int> addDict;
    private readonly delegate* unmanaged[Cdecl]<byte*, int> removeDict;
    private readonly delegate* unmanaged[Cdecl]<byte*, int> setDictOrder;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, int, byte*, byte*> lookup;
    private readonly delegate* unmanaged[Cdecl]<byte*, int, byte*, byte*> segment;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*> kanji;
    private readonly delegate* unmanaged[Cdecl]<byte*> tags;
    private readonly delegate* unmanaged[Cdecl]<byte*> styles;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, int> media;
    private readonly delegate* unmanaged[Cdecl]<byte*> mediaData;
    private readonly delegate* unmanaged[Cdecl]<byte*> version;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*> import;
    private bool disposed;

    public NativeEngine(string path)
    {
        library = NativeLibrary.Load(path);
        try
        {
            lastError = (delegate* unmanaged[Cdecl]<byte*>)Export("hdw_last_error");
            reset = (delegate* unmanaged[Cdecl]<void>)Export("hdw_reset");
            addDict = (delegate* unmanaged[Cdecl]<byte*, int, int, int, int>)Export("hdw_add_dict");
            removeDict = (delegate* unmanaged[Cdecl]<byte*, int>)Export("hdw_remove_dict");
            setDictOrder = (delegate* unmanaged[Cdecl]<byte*, int>)Export("hdw_set_dict_order");
            lookup = (delegate* unmanaged[Cdecl]<byte*, int, int, byte*, byte*>)Export("hdw_lookup");
            segment = (delegate* unmanaged[Cdecl]<byte*, int, byte*, byte*>)Export("hdw_segment");
            kanji = (delegate* unmanaged[Cdecl]<byte*, byte*>)Export("hdw_kanji");
            tags = (delegate* unmanaged[Cdecl]<byte*>)Export("hdw_tags");
            styles = (delegate* unmanaged[Cdecl]<byte*>)Export("hdw_styles");
            media = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)Export("hdw_media");
            mediaData = (delegate* unmanaged[Cdecl]<byte*>)Export("hdw_media_data");
            version = (delegate* unmanaged[Cdecl]<byte*>)Export("hdw_native_version");
            import = (delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*>)Export("hdw_import");
            var init = (delegate* unmanaged[Cdecl]<int>)Export("hdw_native_init");
            if (init() != 1)
                DiagnosticLog.Write("offline", "hdw_native could not switch CRT paths to UTF-8; non-ASCII profile paths may not open on this OS/Wine build.");
            Version = NativeVersion.Parse(Utf8(version()));
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    /// <summary>Loads the DLL that ships next to the plugin assembly.</summary>
    public static NativeEngine LoadFrom(string directory) => new(Path.Combine(directory, LibraryName));

    public object Gate { get; } = new();

    public NativeVersion Version { get; }

    private nint Export(string name) => NativeLibrary.GetExport(library, name);

    // ------------------------------------------------------------------ calls

    public string LastError
    {
        get
        {
            lock (Gate)
            {
                ThrowIfDisposed();
                return Utf8(lastError());
            }
        }
    }

    public void Reset()
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            reset();
        }
    }

    /// <summary>Adds one kind of an imported dictionary folder. Null on success, else the engine's error.</summary>
    public string? AddDictionary(string path, Kind kind)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            fixed (byte* p = Utf8Z(path))
            {
                // Mapped entries and index: the OS pages the files in on demand.
                return addDict(p, (int)kind, 0, 0) == 1 ? null : NonEmpty(Utf8(lastError()), "the engine rejected the dictionary");
            }
        }
    }

    public int RemoveDictionary(string path)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            fixed (byte* p = Utf8Z(path))
                return removeDict(p);
        }
    }

    public bool SetDictionaryOrder(JsonArray paths)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            fixed (byte* p = Utf8Z(paths.ToJsonString()))
                return setDictOrder(p) == 1;
        }
    }

    public string Lookup(string text, int maxResults, int scanLength, string optionsJson) =>
        Call("hdw_lookup", () =>
        {
            fixed (byte* t = Utf8Z(text))
            fixed (byte* o = Utf8Z(optionsJson))
                return Utf8(lookup(t, maxResults, scanLength, o));
        });

    public string Segment(string text, int scanLength, string optionsJson) =>
        Call("hdw_segment", () =>
        {
            fixed (byte* t = Utf8Z(text))
            fixed (byte* o = Utf8Z(optionsJson))
                return Utf8(segment(t, scanLength, o));
        });

    public string Kanji(string character) =>
        Call("hdw_kanji", () =>
        {
            fixed (byte* c = Utf8Z(character))
                return Utf8(kanji(c));
        });

    public string Tags() => Call("hdw_tags", () => Utf8(tags()));

    /// <summary>The loaded dictionaries' stylesheets: <c>[{dictionary, styles}]</c>.</summary>
    public string Styles() => Call("hdw_styles", () => Utf8(styles()));

    /// <summary>A media file out of a loaded dictionary's media.bin, by dictionary title; null when absent.</summary>
    public byte[]? Media(string dictionary, string path) =>
        Call("hdw_media", () =>
        {
            fixed (byte* d = Utf8Z(dictionary))
            fixed (byte* p = Utf8Z(path))
            {
                var length = media(d, p);
                if (length <= 0) return null;
                var data = mediaData();
                return data == null ? null : new ReadOnlySpan<byte>(data, length).ToArray();
            }
        });

    /// <summary>
    /// Imports a Yomitan dictionary archive into <c>outDirectory/&lt;title&gt;</c>
    /// (hdw_import). The plugin downloads packages already imported by
    /// Hachidori; this is for tests that need one without a host.
    /// </summary>
    public JsonObject Import(string archive, string outDirectory)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            fixed (byte* a = Utf8Z(archive))
            fixed (byte* o = Utf8Z(outDirectory))
                return JsonNode.Parse(Utf8(import(a, o, 0))) as JsonObject ?? new JsonObject();
        }
    }

    // hdw_last_error is cleared on entry to every call, so a non-empty one after
    // the call means this call failed (engine-service.js throwIfEngineFailed).
    private T Call<T>(string name, Func<T> call)
    {
        lock (Gate)
        {
            ThrowIfDisposed();
            var result = call();
            var error = Utf8(lastError());
            if (error.Length > 0)
                throw new OfflineEngineException($"{name} failed: {error}");
            return result;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

    internal static byte[] Utf8Z(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static string Utf8(byte* value) => value == null ? string.Empty : Marshal.PtrToStringUTF8((nint)value) ?? string.Empty;

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            // Unmap every dictionary before the module goes, so a plugin reload
            // (which loads the DLL again) starts from an empty engine.
            reset();
            NativeLibrary.Free(library);
        }
    }
}

/// <summary>What hdw_native.dll was built from (native/sources.json).</summary>
public sealed record NativeVersion(string Hachidori, string HachidoriCommit, string HoshidictsCommit, int DictionaryFormat)
{
    public static NativeVersion Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        return new NativeVersion(
            node["hachidori"]?.ToString() ?? "unknown",
            node["hachidoriCommit"]?.ToString() ?? "unknown",
            node["hoshidictsCommit"]?.ToString() ?? "unknown",
            node["dictionaryFormat"] is JsonValue v && v.TryGetValue<int>(out var format) ? format : 0);
    }

    public string Short => $"Hachidori {Hachidori} engine (hoshidicts {HoshidictsCommit[..Math.Min(7, HoshidictsCommit.Length)]})";
}

public sealed class OfflineEngineException(string message) : Exception(message)
{
    /// <summary>Retrying cannot help (as opposed to a network or relay failure).</summary>
    public bool Permanent { get; init; }
}
