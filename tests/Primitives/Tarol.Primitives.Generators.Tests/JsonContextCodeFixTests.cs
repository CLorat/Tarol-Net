using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Tarol.Primitives.CodeFixes;

namespace Tarol.Primitives.Generators.Tests;

/// <summary>The code fixes of TPRM035 (add the entry) and TPRM036 (remove it), through Roslyn's public code-fix API.</summary>
public class JsonContextCodeFixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Context = """
        using System; using System.Collections.Generic; using System.Text.Json.Serialization;
        namespace Shop;

        // the shop's JSON types
        [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        ENTRIES
        public sealed partial class ShopJson : JsonSerializerContext;
        """;

    private static (AdhocWorkspace Workspace, DocumentId Context) Workspace(string entries, string register = "")
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Shop", "Shop", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: GeneratorTestHost.Parse, metadataReferences: GeneratorTestHost.References));
        workspace.AddDocument(project.Id, "Stubs.cs", SourceText.From(JsonContextDiagnosticTests.Stubs));
        workspace.AddDocument(project.Id, "Shop.cs", SourceText.From(JsonContextDiagnosticTests.Shop
            .Replace("CONTEXT", "", StringComparison.Ordinal).Replace("REGISTER", register, StringComparison.Ordinal)
            .Replace("public sealed partial class ShopJson : JsonSerializerContext;", "", StringComparison.Ordinal)));
        var context = workspace.AddDocument(project.Id, "ShopJson.cs", SourceText.From(Context.Replace("ENTRIES", entries, StringComparison.Ordinal)));
        return (workspace, context.Id);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(Project project, string id)
    {
        var compilation = (await project.GetCompilationAsync(Ct))!;
        var all = await compilation.WithAnalyzers([new JsonContextAnalyzer()]).GetAnalyzerDiagnosticsAsync(Ct);
        return [.. all.Where(d => d.Id == id)];
    }

    private static async Task<Solution> Fix(CodeFixProvider provider, Solution solution, Diagnostic diagnostic)
    {
        var document = solution.GetDocument(diagnostic.Location.SourceTree)!;
        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), Ct));
        var operations = await actions.Single().GetOperationsAsync(Ct);
        return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
    }

    private static async Task<Solution> FixAll(CodeFixProvider provider, Solution solution, ImmutableArray<Diagnostic> diagnostics)
    {
        var document = solution.GetDocument(diagnostics[0].Location.SourceTree)!;
        var context = new FixAllContext(document, provider, FixAllScope.Project, null, provider.FixableDiagnosticIds, new Provider(diagnostics), Ct);
        var action = await provider.GetFixAllProvider()!.GetFixAsync(context);
        var operations = await action!.GetOperationsAsync(Ct);
        return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
    }

    private sealed class Provider(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            return diagnostics.Where(d => d.Location.SourceTree == tree);
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Diagnostic>>([]);
    }

    private static async Task<string> Text(Solution solution, DocumentId id) => (await solution.GetDocument(id)!.GetTextAsync(Ct)).ToString();

    [Fact]
    public async Task One_fix_adds_the_entry_to_the_named_context_with_a_short_type_name()
    {
        var (workspace, context) = Workspace("[JsonSerializable(typeof(int))]");
        var missing = await Analyze(workspace.CurrentSolution.Projects.Single(), "TPRM035");
        var added = missing.Single(d => d.Properties[JsonContextAnalyzer.TypeKey] == "global::Shop.AddBlock");

        var fixedSolution = await Fix(new AddToJsonContext(), workspace.CurrentSolution, added);

        var text = await Text(fixedSolution, context);
        text.ShouldContain("[JsonSerializable(typeof(AddBlock))]");
        text.ShouldContain("public sealed partial class ShopJson");
        (await Analyze(fixedSolution.Projects.Single(), "TPRM035")).Select(d => d.Properties[JsonContextAnalyzer.TypeKey]).ShouldNotContain("global::Shop.AddBlock");
    }

    [Fact]
    public async Task Fix_all_adds_every_missing_type_once_with_the_pinned_names_and_leaves_nothing_to_report()
    {
        var (workspace, context) = Workspace("[JsonSerializable(typeof(int))]", ".Add<Settings>()");
        var missing = await Analyze(workspace.CurrentSolution.Projects.Single(), "TPRM035");
        missing.Length.ShouldBe(5);

        var fixedSolution = await FixAll(new AddToJsonContext(), workspace.CurrentSolution, missing);

        var text = await Text(fixedSolution, context);
        text.ShouldContain("[JsonSerializable(typeof(Month.Started), TypeInfoPropertyName = \"MonthStarted\")]");
        text.ShouldContain("[JsonSerializable(typeof(Settings.Started), TypeInfoPropertyName = \"SettingsStarted\")]");
        text.ShouldContain("[JsonSerializable(typeof(List<Block>))]");
        text.ShouldContain("// the shop's JSON types");
        (await Analyze(fixedSolution.Projects.Single(), "TPRM035")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_removal_takes_the_entry_or_its_list_and_keeps_the_rest()
    {
        var (workspace, context) = Workspace("""
            [JsonSerializable(typeof(Month.Started)), JsonSerializable(typeof(Month.Added)), JsonSerializable(typeof(DateTimeOffset))]
            // the block
            [JsonSerializable(typeof(Block))]
            [JsonSerializable(typeof(List<Block>)), JsonSerializable(typeof(AddBlock))]
            """);
        var redundant = await Analyze(workspace.CurrentSolution.Projects.Single(), "TPRM036");
        redundant.Length.ShouldBe(2);                                   // DateTimeOffset and Block (through Added); List<Block> is the state's

        var fixedSolution = await FixAll(new RemoveFromJsonContext(), workspace.CurrentSolution, redundant);

        var text = await Text(fixedSolution, context);
        text.ShouldNotContain("typeof(Block)");
        text.ShouldNotContain("DateTimeOffset");
        text.ShouldContain("[JsonSerializable(typeof(Month.Started)), JsonSerializable(typeof(Month.Added))]");
        text.ShouldContain("// the block");
        text.ShouldContain("[JsonSerializable(typeof(List<Block>)), JsonSerializable(typeof(AddBlock))]");
        (await Analyze(fixedSolution.Projects.Single(), "TPRM036")).ShouldBeEmpty();
        (await Analyze(fixedSolution.Projects.Single(), "TPRM035")).ShouldBeEmpty();
    }
}
