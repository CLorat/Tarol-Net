using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Search.Generators;

/// <summary>
///     Implements <c>ISearchable&lt;T&gt;</c> on every <c>[Searchable]</c> aggregate: the shape (types, fingerprint) and the
///     map from the loaded aggregate to its documents and its entities'. Diagnostics TSRCH001–TSRCH005.
///     <para>
///         The transform yields a symbol-free, location-free <see cref="SearchModel"/>, so editing a method body or an unrelated
///         file regenerates nothing (incrementality tests).
///     </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SearchGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var discovered = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.AttributeName, static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => Discovery.Transform(ctx, ct));

        context.RegisterSourceOutput(discovered.Where(static d => !d.Diagnostics.IsEmpty), static (spc, d) =>
        {
            foreach (var diagnostic in d.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var models = discovered.Where(static d => d.Model is not null).Select(static (d, _) => d.Model!).WithTrackingName("SearchModels");
        context.RegisterSourceOutput(models, static (spc, model) => spc.AddSource(model.HintName, Emitter.Searchable(model)));
    }
}
