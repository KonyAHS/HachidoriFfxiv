using System.Text;

namespace HachidoriFfxiv.ArchCheck;

internal sealed class AllowList
{
    private readonly HashSet<string> _entries;

    private AllowList(HashSet<string> entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Count;

    public static string EntryFor(ArchDiagnostic diagnostic) => $"{diagnostic.Lint} {diagnostic.Key}";

    public static AllowList FromDiagnostics(IEnumerable<ArchDiagnostic> diagnostics) =>
        new(new HashSet<string>(diagnostics.Select(EntryFor), StringComparer.Ordinal));

    public static AllowList Read(string path)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            entries.Add(line);
        }

        return new AllowList(entries);
    }

    public bool IsAllowed(ArchDiagnostic diagnostic) => _entries.Contains(EntryFor(diagnostic));

    public List<string> FindFixedEntries(IEnumerable<ArchDiagnostic> currentDiagnostics)
    {
        var current = new HashSet<string>(currentDiagnostics.Select(EntryFor), StringComparer.Ordinal);
        return _entries
            .Where(e => !current.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
    }

    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Baseline architectural debt for HachidoriFfxiv.ArchCheck.");
        sb.AppendLine("# Do not add new entries casually; fix new violations from first principles.");
        sb.AppendLine("# Regenerate after fixing debt: dotnet run --project tests/HachidoriFfxiv.ArchCheck -c Release -- --update-allow .deslop.allow");
        sb.AppendLine();

        foreach (var entry in _entries.OrderBy(e => e, StringComparer.Ordinal))
        {
            sb.AppendLine(entry);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
