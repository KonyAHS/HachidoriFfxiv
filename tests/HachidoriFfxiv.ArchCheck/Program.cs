namespace HachidoriFfxiv.ArchCheck;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"arch-check: {ex.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        string? repoRoot = null;
        var sourceDir = "HachidoriFfxiv";
        string? allowPath = null;
        string? updateAllowPath = null;
        var strictAllow = false;
        var runSelfTest = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                case "--self-test":
                    runSelfTest = true;
                    break;
                case "--strict-allow":
                    strictAllow = true;
                    break;
                case "--repo":
                    repoRoot = NextValue(args, ref i, arg);
                    break;
                case "--source":
                    sourceDir = NextValue(args, ref i, arg);
                    break;
                case "--allow":
                    allowPath = NextValue(args, ref i, arg);
                    break;
                case "--update-allow":
                    updateAllowPath = NextValue(args, ref i, arg);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (allowPath is not null && updateAllowPath is not null)
        {
            throw new ArgumentException("Cannot combine --allow and --update-allow.");
        }

        if (runSelfTest)
        {
            SelfTest.Run();
            if (allowPath is null && updateAllowPath is null && repoRoot is null)
            {
                return 0;
            }
        }

        var resolvedRepo = repoRoot is not null ? Path.GetFullPath(repoRoot) : DiscoverRepoRoot();
        var model = ModelBuilder.LoadFromDisk(resolvedRepo, sourceDir);
        var all = Lints.CheckAll(model);

        if (updateAllowPath is not null)
        {
            var targetPath = ResolveRelativeToRepo(resolvedRepo, updateAllowPath);
            var allow = AllowList.FromDiagnostics(all);
            allow.Write(targetPath);
            Console.WriteLine($"arch-check: wrote {allow.Count} entries to {updateAllowPath}");
            return 0;
        }

        if (allowPath is not null)
        {
            var resolvedAllow = ResolveRelativeToRepo(resolvedRepo, allowPath);
            if (!File.Exists(resolvedAllow))
            {
                throw new FileNotFoundException($"Allowlist not found: {resolvedAllow}");
            }

            var allow = AllowList.Read(resolvedAllow);
            var active = all.Where(d => !allow.IsAllowed(d)).ToList();
            var fixedEntries = allow.FindFixedEntries(all);

            Console.Write(Lints.FormatReport(active, fixedEntries.Count, allowPath));
            if (active.Count > 0)
            {
                return 1;
            }

            if (strictAllow && fixedEntries.Count > 0)
            {
                Console.Error.WriteLine("arch-check: --strict-allow failed because stale entries remain in the allowlist:");
                foreach (var entry in fixedEntries)
                {
                    Console.Error.WriteLine($"  stale: {entry}");
                }
                return 1;
            }

            return 0;
        }

        Console.Write(Lints.FormatReport(all, fixedAllowedCount: 0, allowPath: null));
        return all.Count == 0 ? 0 : 1;
    }

    private static string NextValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        return args[++index];
    }

    private static string ResolveRelativeToRepo(string repoRoot, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path);

    private static string DiscoverRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HachidoriFfxiv.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate HachidoriFfxiv.sln in any parent directory.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            usage: HachidoriFfxiv.ArchCheck [options]

            Architectural linter inspired by deslop-please (module-cycle, tangle, wide-directory, long-file).

            options:
              --repo <path>          Repository root containing HachidoriFfxiv.sln (auto-discovered by default)
              --source <relpath>     Source folder to inspect (default: HachidoriFfxiv)
              --allow <path>         Suppress listed baseline debt; fail on new errors
              --update-allow <path>  Write current violations to <path>
              --strict-allow         Fail when allowlisted entries have already been fixed
              --self-test            Run built-in unit tests for all lint rules
            """);
    }
}
