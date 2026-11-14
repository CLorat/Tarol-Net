using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tarol.Primitives.Generators;

/// <summary>
///     Generates, for every <c>partial class X : AggregateRoot&lt;X&gt;</c>, the dispatch that applies its events live and on
///     replay, the identity maps and lifecycle of its child entities (<c>Entity&lt;X, TChild&gt;</c>), <c>Rehydrate</c>,
///     <c>EventTypeOf</c> and the invariant check; and for each entity its constructor, apply switch and
///     <c>IOwnedBy&lt;TOwner&gt;</c> interfaces.
///     <para>
///         The transform turns each declaration into a symbol-free <see cref="TypeModel"/>, cached per type. An aggregate's
///         code depends on all of its types (owners, handlers, events in other files), so the small models are collected
///         once and grouped per root; the equatable <see cref="AggregateModel"/> makes each aggregate regenerate only when
///         one of its own types changed (incrementality tests).
///     </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AggregateGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => Discovery.IsCandidate(node), static (ctx, ct) => Discovery.Transform(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(types.Where(static t => t.Diagnostics.Count > 0), static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var aggregates = types.Collect().SelectMany(static (all, ct) => Group(all, ct));

        // Diagnostics need locations; the source does not. Emitting from a location-free copy keeps the generated file
        // cached when an edit only moves text (incrementality tests).
        context.RegisterSourceOutput(aggregates, static (spc, aggregate) =>
        {
            try
            {
                var emitter = new Emitter(aggregate);
                emitter.Check();
                foreach (var diagnostic in emitter.Diagnostics)
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.GeneratorFailure, aggregate.Root.Location?.ToLocation(), aggregate.Root.Name, e.Message));
            }
        });

        context.RegisterSourceOutput(aggregates.Select(static (a, _) => a.WithoutLocations()), static (spc, aggregate) =>
        {
            string? source;
            try
            {
                source = new Emitter(aggregate).Emit();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _ = e; // reported by the diagnostics output above
                return;
            }

            if (source is not null)
                spc.AddSource(aggregate.Root.HintName, SourceText.From(source, Encoding.UTF8));
        });
    }

    private static ImmutableArray<AggregateModel> Group(ImmutableArray<TypeModel> all, CancellationToken ct)
    {
        var builder = ImmutableArray.CreateBuilder<AggregateModel>();
        foreach (var root in all.Where(static t => t.Kind == TypeKind.Root).OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var entities = all.Where(t => t.Kind == TypeKind.Entity && t.RootKey == root.FullName)
                .OrderBy(static t => t.FullName, StringComparer.Ordinal).ToImmutableArray();
            var events = all.Where(t => t.Kind == TypeKind.Event && t.RootKey == root.FullName)
                .OrderBy(static t => t.FullName, StringComparer.Ordinal).ToImmutableArray();
            builder.Add(new AggregateModel(root, new EquatableArray<TypeModel>(entities), new EquatableArray<TypeModel>(events)));
        }

        return builder.ToImmutable();
    }
}
