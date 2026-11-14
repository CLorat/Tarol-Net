using Microsoft.CodeAnalysis;

namespace Tarol.Changes.Generators;

/// <summary>
///     Generates, for every change (a type implementing <c>IChange&lt;T&gt;</c>, <c>ICreation&lt;T&gt;</c> or <c>ILoads&lt;T&gt;</c>),
///     <c>IChange&lt;T&gt;.Target</c> from its only <c>Id&lt;T&gt;</c> member and the loads of its <c>[Read]</c> members; and per
///     assembly a typed <c>GeneratedChanges.Dispatch(object)</c>. Diagnostics TCHG001–TCHG005.
///     <para>
///         The transform yields a symbol-free, location-free <see cref="ChangeModel"/> (names and types, never bodies), so
///         editing an <c>Execute</c> body or an unrelated file regenerates nothing (incrementality tests).
///     </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ChangesGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var discovered = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => Discovery.IsCandidate(node), static (ctx, ct) => Discovery.Transform(ctx, ct))
            .Where(static d => d.Model is not null || !d.Diagnostics.IsEmpty);

        context.RegisterSourceOutput(discovered.Where(static d => !d.Diagnostics.IsEmpty), static (spc, d) =>
        {
            foreach (var diagnostic in d.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var models = discovered.Where(static d => d.Model is not null).Select(static (d, _) => d.Model!).WithTrackingName("ChangeModels");

        context.RegisterSourceOutput(models.Where(static m => m.NeedsPartial), static (spc, model) =>
            spc.AddSource(Emitter.HintName(model), Emitter.Change(model)));

        var assembly = context.CompilationProvider.Select(static (c, _) => c.AssemblyName);
        var entries = models.Select(static (m, _) => new DispatchEntry(m.FullName, m.Depth)).Collect();
        context.RegisterSourceOutput(entries.Combine(assembly), static (spc, pair) =>
        {
            if (pair.Left.IsEmpty)
                return;
            spc.AddSource("GeneratedChanges.g.cs", Emitter.Dispatch(pair.Left, pair.Right));
        });
    }
}
