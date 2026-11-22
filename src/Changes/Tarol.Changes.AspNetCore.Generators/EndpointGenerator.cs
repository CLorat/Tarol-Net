using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Changes.AspNetCore.Generators;

/// <summary>
///     Generates the HTTP entries (docs/architecture/rest.md): for every <c>[Endpoint]</c> change or view and every
///     <c>[Webhook]</c> factory, a <c>RequestDelegate</c>; per assembly <c>GeneratedEndpoints.MapChanges()</c>, and, when the
///     application references <c>Microsoft.AspNetCore.OpenApi</c>, <c>GeneratedOpenApi.AddTarolEndpoints(OpenApiOptions)</c>
///     (with <c>x-tarol-topics</c> when it also references <c>Tarol.Messaging.SignalR</c>).
///     The Request Delegate Generator does not see generated <c>MapPost</c> calls (C-12), so the delegates bind by
///     themselves. Diagnostics TWEB001–TWEB007. Models are symbol-free; a body edit or an unrelated file regenerates nothing.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var entries = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.EndpointAttribute, static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => Discovery.Entry(ctx, ct))
            .WithTrackingName("Entries");
        context.RegisterSourceOutput(entries.Where(static e => !e.Diagnostics.IsEmpty), static (spc, e) =>
        {
            foreach (var diagnostic in e.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });
        var models = entries.Where(static e => e.Model is not null).Select(static (e, _) => e.Model!).Collect();
        var valueObjects = entries.SelectMany(static (e, _) => e.ValueObjects).Collect();
        var memberRules = entries.SelectMany(static (e, _) => e.MemberRules).Collect();

        var hooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(Discovery.WebhookAttribute, static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, ct) => Discovery.Hooks(ctx, ct))
            .SelectMany(static (h, _) => h)
            .WithTrackingName("Webhooks");
        context.RegisterSourceOutput(hooks.Where(static h => h.Diagnostic is not null), static (spc, h) => spc.ReportDiagnostic(h.Diagnostic!.ToDiagnostic()));
        var hookModels = hooks.Where(static h => h.Model is not null).Select(static (h, _) => h.Model!).Collect();

        var assembly = context.CompilationProvider.Select(static (c, _) => c.AssemblyName);
        var openApi = context.CompilationProvider.Select(static (c, _) => c.GetTypeByMetadataName("Microsoft.AspNetCore.OpenApi.OpenApiOptions") is not null);
        // BC-25: with Tarol.Messaging.SignalR referenced, the document also lists the UI push's topics (x-tarol-topics)
        var pushTopics = context.CompilationProvider.Select(static (c, _) => c.GetTypeByMetadataName("Tarol.Messaging.SignalR.UiPushTopics") is not null);

        context.RegisterSourceOutput(models.Combine(hookModels).Combine(assembly), static (spc, all) =>
        {
            var ((entryModels, hookList), name) = all;
            var duplicates = Discovery.Duplicates(entryModels, hookList).ToArray();
            foreach (var diagnostic in duplicates)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            if ((entryModels.IsEmpty && hookList.IsEmpty) || duplicates.Length > 0)
                return;
            var ordered = entryModels.OrderBy(e => e.Pattern, System.StringComparer.Ordinal).ThenBy(e => e.Verb, System.StringComparer.Ordinal)
                .ThenBy(e => e.FullName, System.StringComparer.Ordinal).ToImmutableArray();
            spc.AddSource("GeneratedEndpoints.g.cs", Emitter.Endpoints(ordered, hookList, name));
        });

        context.RegisterSourceOutput(models.Combine(valueObjects).Combine(memberRules).Combine(openApi).Combine(pushTopics).Combine(assembly), static (spc, all) =>
        {
            var (((((entryModels, objects), rules), available), topics), name) = all;
            if (!available || entryModels.IsEmpty)
                return;
            spc.AddSource("GeneratedOpenApi.g.cs", OpenApiEmitter.Emit(entryModels, objects, rules, name, topics));
        });
    }
}
