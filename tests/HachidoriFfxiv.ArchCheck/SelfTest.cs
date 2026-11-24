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

        // Sibling namespace cycle is caught as module-cycle.
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

        // A one-way dependency remains valid even with an unused using in the other direction.
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
            Assert(diags.Count == 0, "one-way namespace dependencies pass even with unused using directives");
        }

        // Parent and child namespaces can form a type tangle without a sibling module-cycle.
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

        // Small same-namespace type cycles are allowed. A cycle of 8 types is a tangle.
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

        // Eight types would exceed the tangle limit if nested types were counted as peers.
        {
            var model = ModelBuilder.BuildFromSources("App", [
                ("App/Core/PartA.cs", """
                    namespace App.Core;
                    public partial class Container
                    {
                        public Inner1? Child;
                        public sealed class Inner1 { public Inner2? Next; }
                        public sealed class Inner2 { public Inner3? Next; }
                        public sealed class Inner3 { public Inner4? Next; }
                        public sealed class Inner4 { public Inner5? Next; }
                    }
                    """),
                ("App/Core/PartB.cs", """
                    namespace App.Core;
                    public partial class Container
                    {
                        public int Count => Child is null ? 0 : 1;
                        public sealed class Inner5 { public Inner6? Next; }
                        public sealed class Inner6 { public Inner7? Next; }
                        public sealed class Inner7 { public Container? Owner; }
                    }
                    """),
            ]);
            Assert(Lints.CheckAll(model).Count == 0,
                "nested types and partial classes never self-tangle");
        }

        // wide-directory (> 15 members) and long-file (> 2000 lines).
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

        // The allowlist suppresses known debt and detects fixed entries.
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
