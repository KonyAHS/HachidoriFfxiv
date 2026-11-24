namespace HachidoriFfxiv.ArchCheck;

internal static class SelfTest
{
    public static void Run()
    {
        var passed = 0;

        void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception($"ArchCheck self-test FAIL: {name}");
            }

            passed++;
        }

        // 1. Clean layered namespaces produce zero diagnostics.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Core/Types.cs", """
                    namespace App.Core;
                    public sealed class Item { public int Value { get; } }
                    """),
                ("App/Feature/Service.cs", """
                    using App.Core;
                    namespace App.Feature;
                    public sealed class Service
                    {
                        public int Read(Item item) => item.Value;
                    }
                    """),
            ]);
            var diags = Lints.CheckAll(model);
            Assert(diags.Count == 0, "clean layered namespaces produce zero errors");
        }

        // 2. Sibling namespace cycle is caught as module-cycle.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Alpha/A.cs", """
                    using App.Beta;
                    namespace App.Alpha;
                    public sealed class A { public B? Other; }
                    """),
                ("App/Beta/B.cs", """
                    using App.Alpha;
                    namespace App.Beta;
                    public sealed class B { public A? Back; }
                    """),
            ]);
            var diags = Lints.CheckAll(model);
            Assert(diags.Any(d => d.Lint == "module-cycle" && d.Key == "App.Alpha,App.Beta"),
                "sibling namespace cycle reports module-cycle");
            Assert(diags.Any(d => d.Lint == "tangle" && d.Key == "App.Alpha.A,App.Beta.B"),
                "mutually referencing types across namespaces report tangle");
        }

        // 3. Unused `using` directive does not fabricate a module-cycle.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Alpha/A.cs", """
                    using App.Beta;
                    namespace App.Alpha;
                    public sealed class A { public int Id; }
                    """),
                ("App/Beta/B.cs", """
                    using App.Alpha;
                    namespace App.Beta;
                    public sealed class B { public A? First; }
                    """),
            ]);
            var diags = Lints.CheckAll(model);
            Assert(!diags.Any(d => d.Lint == "module-cycle"),
                "unused using directive does not trigger false module-cycle");
        }

        // 4. Parent <-> child namespace type cycle is a tangle, NOT a sibling module-cycle.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Host.cs", """
                    using App.Child;
                    namespace App;
                    public sealed class Host { public Worker? Worker; }
                    """),
                ("App/Child/Worker.cs", """
                    namespace App.Child;
                    public sealed class Worker { public Host? Owner; }
                    """),
            ]);
            var diags = Lints.CheckAll(model);
            Assert(!diags.Any(d => d.Lint == "module-cycle"),
                "parent-child namespace usage is not a sibling module-cycle");
            Assert(diags.Any(d => d.Lint == "tangle" && d.Key == "App.Child.Worker,App.Host"),
                "parent-child mutual type reference is caught as tangle");
        }

        // 5. Small same-namespace type cycle (<= 7) is allowed; > 7 is a tangle.
        {
            var smallCycle = ModelBuilder.BuildFromSources("App", [
                ("App/Core/Small.cs", """
                    namespace App.Core;
                    public sealed class One { public Two? Next; }
                    public sealed class Two { public One? Prev; }
                    """),
            ]);
            Assert(!Lints.CheckTangles(smallCycle).Any(),
                "two mutually referencing types in the same namespace are not flagged");

            var bigCycle = ModelBuilder.BuildFromSources("App", [
                ("App/Core/Big.cs", """
                    namespace App.Core;
                    public sealed class T1 { public T2? N; }
                    public sealed class T2 { public T3? N; }
                    public sealed class T3 { public T4? N; }
                    public sealed class T4 { public T5? N; }
                    public sealed class T5 { public T6? N; }
                    public sealed class T6 { public T7? N; }
                    public sealed class T7 { public T8? N; }
                    public sealed class T8 { public T1? N; }
                    """),
            ]);
            Assert(Lints.CheckTangles(bigCycle).Count == 1,
                "cycle of 8 types in one namespace triggers tangle");
        }

        // 6. Nested types and partial classes do not falsely tangle with themselves.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Core/PartA.cs", """
                    namespace App.Core;
                    public partial class Container
                    {
                        public Inner? Child;
                        public sealed class Inner { public Container? Owner; }
                    }
                    """),
                ("App/Core/PartB.cs", """
                    namespace App.Core;
                    public partial class Container
                    {
                        public int Count => Child is null ? 0 : 1;
                    }
                    """),
            ]);
            Assert(Lints.CheckAll(model).Count == 0,
                "nested types and partial classes never self-tangle");
        }

        // 7. wide-directory (> 15 members) and long-file (> 2000 lines).
        {
            var sources = new List<(string, string)>();
            for (var i = 0; i < 16; i++)
            {
                sources.Add(($"App/Wide/F{i:D2}.cs", $"namespace App.Wide; public sealed class C{i} {{}}"));
            }

            var longBody = "namespace App.Long;\npublic sealed class Huge {\n" + new string('\n', 2005) + "}\n";
            sources.Add(("App/Long/Huge.cs", longBody));

            var model = ModelBuilder.BuildFromSources("App", sources);
            var diags = Lints.CheckLayout(model);
            Assert(diags.Any(d => d.Lint == "wide-directory" && d.Key == "App/Wide"),
                "directory with 16 files triggers wide-directory");
            Assert(diags.Any(d => d.Lint == "long-file" && d.Key == "App/Long/Huge.cs"),
                "file over 2000 lines triggers long-file");
        }

        // 8. AllowList suppresses known debt and detects fixed entries.
        {
            var d1 = new ArchDiagnostic("long-file", "App/A.cs", "long", "App/A.cs:1", []);
            var d2 = new ArchDiagnostic("wide-directory", "App/B", "wide", "App/B", []);
            var allow = AllowList.FromDiagnostics([d1, d2]);
            Assert(allow.IsAllowed(d1) && allow.IsAllowed(d2), "allowlist suppresses baseline entries");
            var fixedEntries = allow.FindFixedEntries([d1]);
            Assert(fixedEntries.Count == 1 && fixedEntries[0] == "wide-directory App/B",
                "allowlist reports entries that have been fixed");
        }

        Console.WriteLine($"ArchCheck self-tests: {passed} passed.");
    }
}
