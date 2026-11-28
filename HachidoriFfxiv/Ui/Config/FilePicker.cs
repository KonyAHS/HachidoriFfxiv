using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Lightweight Win32 file picker for selecting one or more Yomitan dictionary .zip archives
/// without adding external UI dependencies. Returns empty on cancel or unsupported platforms.
/// </summary>
internal static class FilePicker
{
    private const int OfnAllowMultiSelect = 0x00000200;
    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public string? lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName lpofn);

    /// <summary>Opens a file dialog to select one or more .zip files. Must be called off the game render thread.</summary>
    public static IReadOnlyList<string> PickZipFiles(string title = "Select Yomitan Dictionary (.zip)")
    {
        if (!OperatingSystem.IsWindows())
            return [];

        const int maxChars = 16384;
        var buffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "Yomitan Dictionary (*.zip)\0*.zip\0All Files (*.*)\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = maxChars,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnAllowMultiSelect | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
            };

            if (!GetOpenFileNameW(ref ofn))
                return [];

            return ParseMultiSelectBuffer(buffer, maxChars);
        }
        catch
        {
            return [];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> ParseMultiSelectBuffer(nint buffer, int maxChars)
    {
        var parts = new List<string>();
        var offset = 0;
        while (offset < maxChars)
        {
            var str = Marshal.PtrToStringUni(buffer + offset * sizeof(char));
            if (string.IsNullOrEmpty(str))
                break;
            parts.Add(str);
            offset += str.Length + 1;
        }

        if (parts.Count == 0)
            return [];
        if (parts.Count == 1)
            return [parts[0]];

        var dir = parts[0];
        var files = new List<string>(parts.Count - 1);
        for (var i = 1; i < parts.Count; i++)
            files.Add(Path.Combine(dir, parts[i]));
        return files;
    }
}
