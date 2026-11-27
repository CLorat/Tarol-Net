using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Tarol.Primitives.Generators;

namespace Tarol.Primitives.CodeFixes;

/// <summary>
/// TPRM035: adds <c>[JsonSerializable(typeof(T))]</c> (with the <c>TypeInfoPropertyName</c> the analyzer chose) to the context the
/// analyzer named. Fix all adds every missing type of the project in one edit per context.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddToJsonContext)), Shared]
public sealed class AddToJsonContext : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("TPRM035");

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => new AddAll();

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.AdditionalLocations.Count == 0 || !diagnostic.Properties.TryGetValue(JsonContextAnalyzer.TypeKey, out var type) || type is null)
                continue;
            var name = diagnostic.Properties.TryGetValue(JsonContextAnalyzer.ContextKey, out var c) ? c : "the context";
            context.RegisterCodeFix(
                CodeAction.Create($"Add to {Short(name)}", ct => Apply(context.Document.Project.Solution, [diagnostic], ct), nameof(AddToJsonContext)),
                diagnostic);
        }
        return Task.CompletedTask;
    }

    private static string Short(string? name) => name is null ? "" : name.Substring(name.LastIndexOf('.') + 1);

    internal static async Task<Solution> Apply(Solution solution, IEnumerable<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        // one edit per context declaration: every entry it receives, in the order reported, once each
        foreach (var group in diagnostics.Where(static d => d.AdditionalLocations.Count > 0 && d.AdditionalLocations[0].SourceTree is not null)
                     .GroupBy(static d => (d.AdditionalLocations[0].SourceTree!, d.AdditionalLocations[0].SourceSpan)))
        {
            var document = solution.GetDocument(group.Key.Item1);
            if (document is null || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
                continue;
            if (root.FindNode(group.Key.SourceSpan).FirstAncestorOrSelf<ClassDeclarationSyntax>() is not { } declaration)
                continue;
            var lists = group.Select(static d => (Type: d.Properties[JsonContextAnalyzer.TypeKey], Name: d.Properties.TryGetValue(JsonContextAnalyzer.NameKey, out var n) ? n : null))
                .Where(static e => e.Type is not null).Distinct().Select(static e => Entry(e.Type!, e.Name)).ToArray();
            var updated = declaration.AddAttributeLists(lists);
            solution = solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(declaration, updated));
            var changed = solution.GetDocument(document.Id)!;
            changed = await Simplifier.ReduceAsync(changed, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            changed = await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            solution = changed.Project.Solution;
        }
        return solution;
    }

    private static AttributeListSyntax Entry(string type, string? name)
    {
        var arguments = SyntaxFactory.SeparatedList(new[] { SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(type))) }
            .Concat(name is null ? [] : [SyntaxFactory.AttributeArgument(
                SyntaxFactory.NameEquals("TypeInfoPropertyName"), null,
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name)))]));
        var attribute = SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::System.Text.Json.Serialization.JsonSerializable"), SyntaxFactory.AttributeArgumentList(arguments));
        return SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute))
            .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);
    }

    private sealed class AddAll : FixAllProvider
    {
        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() => [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];

        public override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
            => Task.FromResult<CodeAction?>(CodeAction.Create("Add every missing type to its JSON context", async ct =>
            {
                var projects = fixAllContext.Scope == FixAllScope.Solution ? fixAllContext.Solution.Projects : [fixAllContext.Project];
                var solution = fixAllContext.Solution;
                foreach (var project in projects)
                {
                    var all = await fixAllContext.GetAllDiagnosticsAsync(project).ConfigureAwait(false);
                    solution = await Apply(solution, all, ct).ConfigureAwait(false);
                }
                return solution;
            }, nameof(AddAll)));
    }
}

/// <summary>TPRM036: removes a <c>[JsonSerializable]</c> entry the context generates anyway (the attribute list when it was its last).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveFromJsonContext)), Shared]
public sealed class RemoveFromJsonContext : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create("TPRM036");

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider()
        => FixAllProvider.Create(static async (context, document, diagnostics) => await Remove(document, diagnostics, context.CancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
            context.RegisterCodeFix(CodeAction.Create("Remove the entry", ct => Remove(context.Document, [diagnostic], ct), nameof(RemoveFromJsonContext)), diagnostic);
        return Task.CompletedTask;
    }

    private static async Task<Document> Remove(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            return document;
        var attributes = diagnostics.Select(d => root.FindNode(d.Location.SourceSpan).FirstAncestorOrSelf<AttributeSyntax>()).Where(static a => a is not null).Cast<AttributeSyntax>().ToList();
        // a list whose every attribute goes is removed whole (its comment stays); otherwise only the attributes
        var groups = attributes.GroupBy(static a => (AttributeListSyntax)a.Parent!).ToList();
        var whole = groups.Where(static g => g.Count() == g.Key.Attributes.Count).Select(static g => (SyntaxNode)g.Key).ToList();
        var some = groups.Where(static g => g.Count() < g.Key.Attributes.Count).SelectMany(static g => g).Cast<SyntaxNode>().ToList();
        var tracked = root.TrackNodes(whole.Concat(some));
        // an attribute goes with its separator and spaces; a list keeps its leading comment, not its line
        tracked = tracked.RemoveNodes(some.Select(n => tracked.GetCurrentNode(n)!), SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        tracked = tracked.RemoveNodes(whole.Select(n => tracked.GetCurrentNode(n)!), SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        return document.WithSyntaxRoot(tracked);
    }
}
