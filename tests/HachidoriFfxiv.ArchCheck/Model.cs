using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HachidoriFfxiv.ArchCheck;

internal sealed record SourceFileInfo(string RelativePath, int LineCount);

internal sealed record TypeInfo(string FullName, string Namespace, string PrimaryFile, int PrimaryLine);

internal sealed record ReferenceEdge(
    string FromNamespace,
    string ToNamespace,
    string FromType,
    string ToType,
    string TargetMember,
    string FilePath,
    int Line);

internal sealed record CodeModel(
    string RepoRoot,
    string SourceRootRelative,
    IReadOnlyList<SourceFileInfo> Files,
    IReadOnlyDictionary<string, TypeInfo> Types,
    IReadOnlyList<ReferenceEdge> References);

internal static class ModelBuilder
{
    private const string ImplicitUsingsTreePath = "<implicit-usings>";

    private static readonly string ImplicitUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
    };

    public static CodeModel LoadFromDisk(string repoRoot, string sourceRootRelative)
    {
        var sourceRootFull = Path.GetFullPath(Path.Combine(repoRoot, sourceRootRelative));
        if (!Directory.Exists(sourceRootFull))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceRootFull}");
        }

        var files = new List<(string RelativePath, string Content)>();
        CollectCsFiles(repoRoot, sourceRootFull, files);
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));

        var extraRefDirs = new List<string>
        {
            Path.Combine(sourceRootFull, "bin", "x64", "Release"),
            Path.Combine(sourceRootFull, "bin", "x64", "Debug"),
        };
        var dalamudLib = Environment.GetEnvironmentVariable("DalamudLibPath");
        if (string.IsNullOrWhiteSpace(dalamudLib))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                dalamudLib = Path.Combine(appData, "XIVLauncher", "addon", "Hooks", "dev");
            }
        }
        if (!string.IsNullOrWhiteSpace(dalamudLib))
        {
            extraRefDirs.Add(dalamudLib);
        }

        return Build(repoRoot, sourceRootRelative, files, extraRefDirs);
    }

    public static CodeModel BuildFromSources(
        string sourceRootRelative,
        IReadOnlyList<(string RelativePath, string Content)> sources)
    {
        return Build(repoRoot: "", sourceRootRelative, sources, extraRefDirs: []);
    }

    private static CodeModel Build(
        string repoRoot,
        string sourceRootRelative,
        IReadOnlyList<(string RelativePath, string Content)> sources,
        IReadOnlyList<string> extraRefDirs)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var trees = new List<SyntaxTree>(sources.Count + 1)
        {
            CSharpSyntaxTree.ParseText(ImplicitUsingsSource, parseOptions, ImplicitUsingsTreePath),
        };
        var fileInfos = new List<SourceFileInfo>(sources.Count);

        foreach (var (relativePath, content) in sources)
        {
            var normalized = NormalizePath(relativePath);
            trees.Add(CSharpSyntaxTree.ParseText(content, parseOptions, normalized));
            fileInfos.Add(new SourceFileInfo(normalized, CountLines(content)));
        }

        var references = CollectMetadataReferences(extraRefDirs, Path.GetFileName(sourceRootRelative) + ".dll");
        var compilation = CSharpCompilation.Create(
            assemblyName: "ArchCheckTarget",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        var edges = new List<ReferenceEdge>();

        foreach (var tree in trees)
        {
            if (tree.FilePath == ImplicitUsingsTreePath)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            var walker = new ReferenceWalker(compilation, semanticModel, tree.FilePath, types, edges);
            walker.Visit(tree.GetRoot());
        }

        return new CodeModel(repoRoot, NormalizePath(sourceRootRelative).TrimEnd('/'), fileInfos, types, edges);
    }

    private static void CollectCsFiles(string repoRoot, string directory, List<(string RelativePath, string Content)> output)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var rel = NormalizePath(Path.GetRelativePath(repoRoot, file));
            output.Add((rel, File.ReadAllText(file)));
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(subDir);
            if (ExcludedDirectories.Contains(name))
            {
                continue;
            }

            CollectCsFiles(repoRoot, subDir, output);
        }
    }

    private static List<MetadataReference> CollectMetadataReferences(IReadOnlyList<string> extraRefDirs, string selfAssemblyFileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        void TryAdd(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var fileName = Path.GetFileName(path);
            if (fileName.Equals(selfAssemblyFileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!seen.Add(fileName))
            {
                return;
            }

            try
            {
                // Skip native DLLs (e.g., cimgui.dll, hdw_native.dll) cleanly.
                AssemblyName.GetAssemblyName(path);
                refs.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // Not a managed assembly or unreadable; safe to ignore for internal-symbol resolution.
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                TryAdd(path);
            }
        }

        foreach (var dir in extraRefDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                TryAdd(dll);
            }
        }

        return refs;
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/');

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        // Trailing newline does not create an extra visible line.
        if (text[^1] == '\n')
        {
            lines--;
        }

        return lines;
    }

    private sealed class ReferenceWalker : CSharpSyntaxWalker
    {
        private readonly CSharpCompilation _compilation;
        private readonly SemanticModel _semanticModel;
        private readonly string _filePath;
        private readonly Dictionary<string, TypeInfo> _types;
        private readonly List<ReferenceEdge> _edges;
        private readonly Stack<string> _namespaces = new();
        private readonly Stack<INamedTypeSymbol> _topTypes = new();

        public ReferenceWalker(
            CSharpCompilation compilation,
            SemanticModel semanticModel,
            string filePath,
            Dictionary<string, TypeInfo> types,
            List<ReferenceEdge> edges)
        {
            _compilation = compilation;
            _semanticModel = semanticModel;
            _filePath = filePath;
            _types = types;
            _edges = edges;
        }

        private string CurrentNamespace => _namespaces.Count > 0 ? _namespaces.Peek() : "";

        private INamedTypeSymbol? CurrentTopType => _topTypes.Count > 0 ? _topTypes.Peek() : null;

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            // Bare `using` statements are not treated as symbol dependencies; actual symbol
            // references in code drive module-cycle and tangle detection.
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var ns = CombineNamespace(CurrentNamespace, node.Name.ToString());
            _namespaces.Push(ns);
            try
            {
                foreach (var member in node.Members)
                {
                    Visit(member);
                }
            }
            finally
            {
                _namespaces.Pop();
            }
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var ns = CombineNamespace(CurrentNamespace, node.Name.ToString());
            _namespaces.Push(ns);
            try
            {
                foreach (var member in node.Members)
                {
                    Visit(member);
                }
            }
            finally
            {
                _namespaces.Pop();
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitClassDeclaration(node));
        public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitStructDeclaration(node));
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitRecordDeclaration(node));
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitInterfaceDeclaration(node));
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitEnumDeclaration(node));
        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node) => VisitTypeDecl(node, () => base.VisitDelegateDeclaration(node));

        private void VisitTypeDecl(MemberDeclarationSyntax node, Action visitChildren)
        {
            var declared = _semanticModel.GetDeclaredSymbol(node) as INamedTypeSymbol;
            var topType = declared is null ? null : OutermostType(declared);
            var pushed = false;

            if (topType is not null)
            {
                RegisterType(topType, node);
                if (_topTypes.Count == 0 || !SymbolEqualityComparer.Default.Equals(_topTypes.Peek(), topType))
                {
                    _topTypes.Push(topType);
                    pushed = true;
                }
            }

            try
            {
                visitChildren();
            }
            finally
            {
                if (pushed)
                {
                    _topTypes.Pop();
                }
            }
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitGenericName(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitConstructorInitializer(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitElementAccessExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitBinaryExpression(node);
        }

        public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitPrefixUnaryExpression(node);
        }

        public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            RecordSymbolReference(node);
            base.VisitPostfixUnaryExpression(node);
        }

        private void RecordSymbolReference(SyntaxNode node)
        {
            var info = _semanticModel.GetSymbolInfo(node);
            if (info.Symbol is not null)
            {
                TryRecordTarget(node, info.Symbol);
                return;
            }

            foreach (var candidate in info.CandidateSymbols)
            {
                TryRecordTarget(node, candidate);
            }
        }

        private void TryRecordTarget(SyntaxNode node, ISymbol rawSymbol)
        {
            var symbol = rawSymbol is IAliasSymbol alias ? alias.Target : rawSymbol;
            symbol = symbol.OriginalDefinition;

            if (symbol is INamespaceSymbol
                or ILocalSymbol
                or IParameterSymbol
                or IRangeVariableSymbol
                or ITypeParameterSymbol
                or ILabelSymbol
                or IDiscardSymbol)
            {
                return;
            }

            if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _compilation.Assembly))
            {
                return;
            }

            if (symbol.DeclaringSyntaxReferences.Length == 0)
            {
                return;
            }

            var targetTopType = symbol is INamedTypeSymbol namedType
                ? OutermostType(namedType)
                : symbol.ContainingType is not null
                    ? OutermostType(symbol.ContainingType)
                    : null;

            if (targetTopType is null)
            {
                return;
            }

            var currentTop = CurrentTopType;
            if (currentTop is not null && SymbolEqualityComparer.Default.Equals(currentTop, targetTopType))
            {
                return;
            }

            var fromType = currentTop is not null ? FormatTypeName(currentTop) : $"<top-level:{_filePath}>";
            var toType = FormatTypeName(targetTopType);
            var fromNs = currentTop is not null ? FormatNamespace(currentTop.ContainingNamespace) : CurrentNamespace;
            var toNs = FormatNamespace(targetTopType.ContainingNamespace);
            var targetMember = FormatMemberName(symbol, targetTopType);
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            _edges.Add(new ReferenceEdge(fromNs, toNs, fromType, toType, targetMember, _filePath, line));
        }

        private void RegisterType(INamedTypeSymbol topType, SyntaxNode node)
        {
            var fullName = FormatTypeName(topType);
            if (_types.ContainsKey(fullName))
            {
                return;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _types[fullName] = new TypeInfo(fullName, FormatNamespace(topType.ContainingNamespace), _filePath, line);
        }

        private static INamedTypeSymbol OutermostType(INamedTypeSymbol type)
        {
            var current = type.OriginalDefinition;
            while (current.ContainingType is not null)
            {
                current = current.ContainingType.OriginalDefinition;
            }

            return current;
        }

        private static string CombineNamespace(string outer, string inner) =>
            string.IsNullOrEmpty(outer) ? inner : $"{outer}.{inner}";

        private static string FormatNamespace(INamespaceSymbol? ns) =>
            ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

        private static string FormatTypeName(INamedTypeSymbol topType)
        {
            var ns = FormatNamespace(topType.ContainingNamespace);
            var name = topType.Arity > 0 ? $"{topType.Name}<{new string(',', topType.Arity - 1)}>" : topType.Name;
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        private static string FormatMemberName(ISymbol symbol, INamedTypeSymbol topType)
        {
            if (symbol is INamedTypeSymbol named)
            {
                return named.Name;
            }

            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            {
                return $"{ctor.ContainingType.Name}.ctor";
            }

            var owner = symbol.ContainingType?.Name ?? topType.Name;
            return $"{owner}.{symbol.Name}";
        }
    }
}
