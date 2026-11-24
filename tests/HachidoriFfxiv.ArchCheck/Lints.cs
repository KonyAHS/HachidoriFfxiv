using System.Text;

namespace HachidoriFfxiv.ArchCheck;

internal sealed record ArchDiagnostic(
    string Lint,
    string Key,
    string Message,
    string? PrimaryLocation,
    IReadOnlyList<string> Notes);

internal static class Lints
{
    public const int HeldInMind = 7;
    public const int MostDirectoryMembers = 15;
    public const int MostFileLines = 2_000;

    public static List<ArchDiagnostic> CheckAll(CodeModel model)
    {
        var diagnostics = new List<ArchDiagnostic>();
        diagnostics.AddRange(CheckModuleCycles(model));
        diagnostics.AddRange(CheckTangles(model));
        diagnostics.AddRange(CheckLayout(model));
        diagnostics.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.Lint, b.Lint);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Key, b.Key);
        });
        return diagnostics;
    }

    /// <summary>
    /// `module-cycle`: sibling namespaces under the same parent namespace that use each other in a cycle.
    /// Parent/child relationships do not form sibling cycles.
    /// </summary>
    public static List<ArchDiagnostic> CheckModuleCycles(CodeModel model)
    {
        var tiesByParent = new SortedDictionary<string, SortedDictionary<(string User, string Used), List<ReferenceEdge>>>(
            StringComparer.Ordinal);

        foreach (var edge in model.References)
        {
            if (!TryGetSiblingPair(edge.FromNamespace, edge.ToNamespace, out var parent, out var user, out var used))
            {
                continue;
            }

            if (!tiesByParent.TryGetValue(parent, out var ties))
            {
                ties = new SortedDictionary<(string, string), List<ReferenceEdge>>(TupleOrdinalComparer.Instance);
                tiesByParent[parent] = ties;
            }

            if (!ties.TryGetValue((user, used), out var list))
            {
                list = [];
                ties[(user, used)] = list;
            }

            list.Add(edge);
        }

        var diagnostics = new List<ArchDiagnostic>();
        foreach (var (parent, ties) in tiesByParent)
        {
            var nodes = ties.Keys
                .SelectMany(k => new[] { k.User, k.Used })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < nodes.Count; i++)
            {
                indexOf[nodes[i]] = i;
            }

            var adj = Enumerable.Range(0, nodes.Count).Select(_ => new List<int>()).ToList();
            foreach (var (user, used) in ties.Keys)
            {
                adj[indexOf[user]].Add(indexOf[used]);
            }

            foreach (var scc in Graph.StronglyConnectedComponents(adj))
            {
                var members = scc.Select(i => nodes[i]).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var memberSet = new HashSet<string>(members, StringComparer.Ordinal);
                var shortNames = members.Select(m => ShortRelativeName(parent, m)).ToList();
                var key = string.Join(",", members);
                var parentLabel = string.IsNullOrEmpty(parent) ? "global namespace" : $"`{parent}`";

                var notes = new List<string>();
                string? primaryLocation = null;

                foreach (var ((user, used), refs) in ties)
                {
                    if (!memberSet.Contains(user) || !memberSet.Contains(used))
                    {
                        continue;
                    }

                    var first = refs.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Line).First();
                    primaryLocation ??= $"{first.FilePath}:{first.Line}";
                    var targets = refs.Select(r => r.TargetMember)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(t => t, StringComparer.Ordinal)
                        .Take(4)
                        .ToList();
                    var extra = refs.Select(r => r.TargetMember).Distinct(StringComparer.Ordinal).Count() - targets.Count;
                    var targetSummary = extra > 0
                        ? $"{string.Join(", ", targets)}, +{extra} more"
                        : string.Join(", ", targets);

                    notes.Add(
                        $"{ShortRelativeName(parent, user)} -> {ShortRelativeName(parent, used)} " +
                        $"({refs.Count} ref{(refs.Count == 1 ? "" : "s")}, e.g. {first.FilePath}:{first.Line}: {targetSummary})");
                }

                diagnostics.Add(new ArchDiagnostic(
                    Lint: "module-cycle",
                    Key: key,
                    Message: $"sibling namespaces in {parentLabel} form a cycle: {string.Join(", ", shortNames)}",
                    PrimaryLocation: primaryLocation,
                    Notes: notes));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// `tangle`: project types that depend on each other in a cycle across more than one namespace,
    /// or a cycle larger than `HeldInMind` (7) types even within one namespace.
    /// </summary>
    public static List<ArchDiagnostic> CheckTangles(CodeModel model)
    {
        var typeEdges = new SortedDictionary<(string User, string Used), List<ReferenceEdge>>(TupleOrdinalComparer.Instance);
        foreach (var edge in model.References)
        {
            if (string.Equals(edge.FromType, edge.ToType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!model.Types.ContainsKey(edge.FromType) || !model.Types.ContainsKey(edge.ToType))
            {
                continue;
            }

            if (!typeEdges.TryGetValue((edge.FromType, edge.ToType), out var list))
            {
                list = [];
                typeEdges[(edge.FromType, edge.ToType)] = list;
            }

            list.Add(edge);
        }

        var nodes = typeEdges.Keys
            .SelectMany(k => new[] { k.User, k.Used })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            indexOf[nodes[i]] = i;
        }

        var adj = Enumerable.Range(0, nodes.Count).Select(_ => new List<int>()).ToList();
        foreach (var (user, used) in typeEdges.Keys)
        {
            adj[indexOf[user]].Add(indexOf[used]);
        }

        var diagnostics = new List<ArchDiagnostic>();
        foreach (var scc in Graph.StronglyConnectedComponents(adj))
        {
            var members = scc.Select(i => nodes[i]).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var memberSet = new HashSet<string>(members, StringComparer.Ordinal);

            var namespaces = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (model.Types.TryGetValue(member, out var info))
                {
                    namespaces.Add(info.Namespace);
                }
            }

            var insideEdges = new List<((string User, string Used) Pair, List<ReferenceEdge> Refs)>();
            foreach (var (pair, refs) in typeEdges)
            {
                if (!memberSet.Contains(pair.User) || !memberSet.Contains(pair.Used))
                {
                    continue;
                }

                insideEdges.Add((pair, refs));
                foreach (var r in refs)
                {
                    namespaces.Add(r.FromNamespace);
                }
            }

            if (namespaces.Count <= 1 && members.Count <= HeldInMind)
            {
                continue;
            }

            var key = string.Join(",", members);
            var reason = namespaces.Count > 1
                ? $"across {namespaces.Count} namespaces ({string.Join(", ", namespaces.Select(n => string.IsNullOrEmpty(n) ? "<global>" : n))})"
                : $"more than {HeldInMind} types in one cycle";

            var notes = new List<string>
            {
                $"members ({members.Count}): {string.Join(", ", members.Select(ShortTypeName))}",
            };

            string? primaryLocation = null;
            foreach (var ((user, used), refs) in insideEdges.Take(12))
            {
                var first = refs.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Line).First();
                primaryLocation ??= $"{first.FilePath}:{first.Line}";
                notes.Add($"{ShortTypeName(user)} -> {ShortTypeName(used)} ({refs.Count} ref{(refs.Count == 1 ? "" : "s")}, e.g. {first.FilePath}:{first.Line})");
            }

            if (insideEdges.Count > 12)
            {
                notes.Add($"... +{insideEdges.Count - 12} more edges");
            }

            diagnostics.Add(new ArchDiagnostic(
                Lint: "tangle",
                Key: key,
                Message: $"{members.Count} types form a dependency tangle {reason}",
                PrimaryLocation: primaryLocation,
                Notes: notes));
        }

        return diagnostics;
    }

    /// <summary>
    /// `long-file` (> 2,000 lines) and `wide-directory` (> 15 direct .cs files + .cs-bearing subdirs).
    /// </summary>
    public static List<ArchDiagnostic> CheckLayout(CodeModel model)
    {
        var diagnostics = new List<ArchDiagnostic>();

        foreach (var file in model.Files)
        {
            if (file.LineCount > MostFileLines)
            {
                diagnostics.Add(new ArchDiagnostic(
                    Lint: "long-file",
                    Key: file.RelativePath,
                    Message: $"`{file.RelativePath}` has {file.LineCount} lines (max {MostFileLines})",
                    PrimaryLocation: $"{file.RelativePath}:1",
                    Notes: []));
            }
        }

        var dirMembers = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in model.Files)
        {
            var path = file.RelativePath;
            var segments = path.Split('/');
            var prefix = model.SourceRootRelative;

            // Only count directories at or below SourceRootRelative.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var dir = string.Join("/", segments[..(i + 1)]);
                if (!string.IsNullOrEmpty(prefix)
                    && !dir.Equals(prefix, StringComparison.Ordinal)
                    && !dir.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var child = i + 1 == segments.Length - 1 ? segments[i + 1] : segments[i + 1] + "/";
                if (!dirMembers.TryGetValue(dir, out var members))
                {
                    members = new SortedSet<string>(StringComparer.Ordinal);
                    dirMembers[dir] = members;
                }

                members.Add(child);
            }
        }

        foreach (var (dir, members) in dirMembers)
        {
            if (members.Count <= MostDirectoryMembers)
            {
                continue;
            }

            var preview = string.Join(", ", members.Take(8));
            var more = members.Count - 8;
            var note = more > 0 ? $"members: {preview}, +{more} more" : $"members: {preview}";
            diagnostics.Add(new ArchDiagnostic(
                Lint: "wide-directory",
                Key: dir,
                Message: $"`{dir}` holds {members.Count} members (max {MostDirectoryMembers})",
                PrimaryLocation: dir,
                Notes: [note]));
        }

        return diagnostics;
    }

    public static string FormatReport(
        IReadOnlyList<ArchDiagnostic> active,
        int fixedAllowedCount,
        string? allowPath)
    {
        var sb = new StringBuilder();
        foreach (var d in active)
        {
            sb.AppendLine($"error[{d.Lint}]: {d.Message}");
            if (!string.IsNullOrEmpty(d.PrimaryLocation))
            {
                sb.AppendLine($"  --> {d.PrimaryLocation}");
            }

            foreach (var note in d.Notes)
            {
                sb.AppendLine($"  = note: {note}");
            }

            sb.AppendLine();
        }

        if (fixedAllowedCount > 0 && allowPath is not null)
        {
            sb.AppendLine(
                $"arch-check: {fixedAllowedCount} allowlisted entr{(fixedAllowedCount == 1 ? "y is" : "ies are")} now fixed; " +
                $"run with --update-allow {allowPath} to prune them.");
        }

        sb.AppendLine(active.Count == 0
            ? "arch-check: 0 new architectural errors."
            : $"arch-check: {active.Count} architectural error{(active.Count == 1 ? "" : "s")} found.");

        return sb.ToString();
    }

    private static bool TryGetSiblingPair(
        string fromNamespace,
        string toNamespace,
        out string parent,
        out string user,
        out string used)
    {
        parent = "";
        user = "";
        used = "";

        if (string.Equals(fromNamespace, toNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        var a = SplitNamespace(fromNamespace);
        var b = SplitNamespace(toNamespace);
        var common = 0;
        var limit = Math.Min(a.Length, b.Length);
        while (common < limit && string.Equals(a[common], b[common], StringComparison.Ordinal))
        {
            common++;
        }

        // Ancestor/descendant relationship (one namespace contains the other): not a sibling cycle.
        if (common == a.Length || common == b.Length)
        {
            return false;
        }

        parent = common == 0 ? "" : string.Join(".", a[..common]);
        user = string.Join(".", a[..(common + 1)]);
        used = string.Join(".", b[..(common + 1)]);
        return true;
    }

    private static string[] SplitNamespace(string ns) =>
        string.IsNullOrEmpty(ns) ? [] : ns.Split('.');

    private static string ShortRelativeName(string parent, string full) =>
        string.IsNullOrEmpty(parent) ? full : full[(parent.Length + 1)..];

    private static string ShortTypeName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    private sealed class TupleOrdinalComparer : IComparer<(string A, string B)>
    {
        public static readonly TupleOrdinalComparer Instance = new();

        public int Compare((string A, string B) x, (string A, string B) y)
        {
            var cmp = StringComparer.Ordinal.Compare(x.A, y.A);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(x.B, y.B);
        }
    }
}
