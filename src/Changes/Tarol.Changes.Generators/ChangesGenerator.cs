using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Changes.Generators;

/// <summary>
///     Generates, for every change (a type implementing <c>IChange&lt;T&gt;</c>, <c>ICreation&lt;T&gt;</c> or <c>ILoads&lt;T&gt;</c>),
///     <c>IChange&lt;T&gt;.Target</c> from its only <c>Id&lt;T&gt;</c> member and the loads of its <c>[Read]</c> members; and per
///     assembly a typed <c>GeneratedChanges.Dispatch(object)</c> and, for <c>[OnEvent&lt;T&gt;]</c> factories, a public
///     <c>GeneratedReactions.Set</c>, and for <c>[Scheduled]</c> factories a public <c>GeneratedJobs.Set</c>. Diagnostics TCHG001–TCHG011.
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

        // reactions: [OnEvent<T>] factories → one public GeneratedReactions per assembly (step 9, sagas.md §2)
        var reactions = context.SyntaxProvider
            .ForAttributeWithMetadataName(ReactionDiscovery.AttributeName, static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => ReactionDiscovery.Transform(ctx, ct))
            .SelectMany(static (found, _) => found);
        context.RegisterSourceOutput(reactions.Where(static r => !r.Diagnostics.IsEmpty), static (spc, r) =>
        {
            foreach (var diagnostic in r.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });
        var reactionModels = reactions.Where(static r => r.Model is not null).Select(static (r, _) => r.Model!).WithTrackingName("ReactionModels").Collect();
        context.RegisterSourceOutput(reactionModels.Combine(assembly), static (spc, pair) =>
        {
            if (!pair.Left.IsEmpty)
                spc.AddSource("GeneratedReactions.g.cs", ReactionEmitter.Emit(pair.Left, pair.Right));
        });
        var sites = reactions.Where(static r => r.Site is not null).Select(static (r, _) => r.Site!).Collect();
        context.RegisterSourceOutput(sites, static (spc, all) =>
        {
            foreach (var diagnostic in ReactionDiscovery.Duplicates(all))
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        // scheduled jobs: [Scheduled] factories → one public GeneratedJobs per assembly (step 14A, jobs.md §3)
        var jobs = context.SyntaxProvider
            .ForAttributeWithMetadataName(JobDiscovery.AttributeName, static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => JobDiscovery.Transform(ctx, ct))
            .SelectMany(static (found, _) => found);
        context.RegisterSourceOutput(jobs.Where(static j => !j.Diagnostics.IsEmpty), static (spc, j) =>
        {
            foreach (var diagnostic in j.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });
        var jobModels = jobs.Where(static j => j.Model is not null).Select(static (j, _) => j.Model!).WithTrackingName("JobModels").Collect();
        context.RegisterSourceOutput(jobModels.Combine(assembly), static (spc, pair) =>
        {
            if (!pair.Left.IsEmpty)
                spc.AddSource("GeneratedJobs.g.cs", JobEmitter.Emit(pair.Left, pair.Right));
        });
        // TN-8: [assembly: GenerateTarolModule] → GeneratedModule.Module
        ModuleGenerator.Initialize(context, reactionModels.Select(static (r, _) => !r.IsEmpty), jobModels.Select(static (j, _) => !j.IsEmpty));

        // TCHG012 (Tatalu FB-INT3-1): a reaction or job name declared in two assemblies this one sees
        var referencedNames = context.CompilationProvider.Select(static (Compilation c, System.Threading.CancellationToken ct) => DeclaredNames.Referenced(c, ct));
        var ownNames = reactionModels.Combine(jobModels).Combine(assembly).Select(static (((ImmutableArray<ReactionModel> Left, ImmutableArray<JobModel> Right) Left, string? Right) pair, System.Threading.CancellationToken _) => new EquatableArray<Declared>(
            pair.Left.Left.Select(r => new Declared("reaction", r.Name, pair.Right ?? ""))
                .Concat(pair.Left.Right.Select(j => new Declared("job", j.Name, pair.Right ?? ""))).ToImmutableArray()));
        context.RegisterSourceOutput(referencedNames.Combine(ownNames), static (spc, pair) =>
        {
            foreach (var diagnostic in DeclaredNames.Collisions(pair.Left.Concat(pair.Right)))
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var jobSites = jobs.Where(static j => j.Site is not null).Select(static (j, _) => j.Site!).Collect();
        context.RegisterSourceOutput(jobSites, static (spc, all) =>
        {
            foreach (var diagnostic in JobDiscovery.Duplicates(all))
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });
    }
}
