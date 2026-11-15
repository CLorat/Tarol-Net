using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Probe.Rest.Gen;

// Prototype of the Tarol.Changes.AspNetCore generator. It reads only user-written declarations ([Endpoint] on a change
// record, an aggregate or a view; [Webhook] on a static From factory) and never another generator's output: the aggregate
// is recognised by its AggregateRoot<T> base, the change by the IChange<T>/ICreation<T> the user wrote. It emits
// RequestDelegates (C-12) and one MapChanges(IEndpointRouteBuilder) in a namespace named after the assembly.
[Generator(LanguageNames.CSharp)]
public sealed class EndpointGenerator : IIncrementalGenerator
{
    private const string EndpointAttr = "Tarol.Changes.AspNetCore.EndpointAttribute";
    private const string WebhookAttr = "Tarol.Changes.AspNetCore.WebhookAttribute";

    private static readonly DiagnosticDescriptor Unsupported = new("TWEB001", "Endpoint not generated",
        "'{0}': {1}", "Tarol.Changes.AspNetCore", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var endpoints = context.SyntaxProvider.ForAttributeWithMetadataName(EndpointAttr,
                static (_, _) => true, static (ctx, ct) => Endpoint.From((INamedTypeSymbol)ctx.TargetSymbol, ctx.Attributes[0], ct))
            .WithTrackingName("Endpoints");
        var hooks = context.SyntaxProvider.ForAttributeWithMetadataName(WebhookAttr,
                static (_, _) => true, static (ctx, _) => Hook.From((IMethodSymbol)ctx.TargetSymbol, ctx.Attributes))
            .SelectMany(static (h, _) => h)
            .WithTrackingName("Webhooks");
        var options = context.AnalyzerConfigOptionsProvider.Select(static (o, _) => new Settings(
            o.GlobalOptions.TryGetValue("build_property.ProbeBinding", out var b) && b == "members" ? "members" : "record",
            o.GlobalOptions.TryGetValue("build_property.ProbeOpenApiMethod", out var m) && m == "true"));
        var name = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "App");

        context.RegisterSourceOutput(endpoints.Collect().Combine(hooks.Collect()).Combine(options).Combine(name), static (spc, all) =>
        {
            var ((( eps, whs), settings), ns) = all;
            foreach (var e in eps.Where(e => e.Error is not null))
                spc.ReportDiagnostic(Diagnostic.Create(Unsupported, null, e.Name, e.Error));
            spc.AddSource("Endpoints.g.cs", Emit(eps.Where(e => e.Error is null).OrderBy(e => e.Pattern, StringComparer.Ordinal).ThenBy(e => e.Verb, StringComparer.Ordinal).ToArray(),
                whs.OrderBy(h => h.Sender, StringComparer.Ordinal).ThenBy(h => h.Type, StringComparer.Ordinal).ThenBy(h => h.Version ?? "", StringComparer.Ordinal).ToArray(), settings, ns));
        });
    }

    private sealed record Settings(string Binding, bool OpenApiMethod);

    internal enum Kind { Change, Creation, State, View }

    internal sealed record Member(string Name, string Type, string? RouteKind, bool Optional, bool OnRoute);

    internal sealed record Endpoint(string Name, string Full, Kind Kind, string Aggregate, string Verb, string Pattern, string? Policy, bool Anonymous,
        Eq<Member> Members, string? Error)
    {
        public static Endpoint From(INamedTypeSymbol type, AttributeData attribute, CancellationToken ct)
        {
            var fq = SymbolDisplayFormat.FullyQualifiedFormat;
            var full = type.ToDisplayString(fq);
            var route = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
            var policy = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Policy").Value.Value as string;
            var anonymous = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Anonymous").Value.Value is true;
            var space = route.IndexOf(' ');
            Endpoint Fail(string why) => new(type.Name, full, Kind.Change, "", "", route, null, false, Eq<Member>.Empty, why);
            if (space <= 0)
                return Fail("the route must be \"VERB /pattern\"");
            var verb = route.Substring(0, space).ToUpperInvariant();
            var pattern = route.Substring(space + 1);
            var placeholders = Placeholders(pattern);

            string? aggregate = null;
            var kind = Kind.Change;
            foreach (var i in type.AllInterfaces)
            {
                var def = i.OriginalDefinition.ToDisplayString();
                if (def == "Tarol.Changes.IChange<T>" || def == "Tarol.Changes.ICreation<T>")
                    (aggregate, kind) = (i.TypeArguments[0].ToDisplayString(fq), def.Contains("ICreation") ? Kind.Creation : Kind.Change);
                else if (def == "Tarol.Changes.AspNetCore.IView<TAggregate, TSelf>")
                    (aggregate, kind) = (i.TypeArguments[0].ToDisplayString(fq), Kind.View);
            }
            for (var b = type.BaseType; aggregate is null && b is not null; b = b.BaseType)
                if (b.OriginalDefinition.ToDisplayString() == "Tarol.Primitives.AggregateRoot<TSelf>"
                    && SymbolEqualityComparer.Default.Equals(b.TypeArguments[0], type))
                    (aggregate, kind) = (full, Kind.State);
            if (aggregate is null)
                return Fail("[Endpoint] needs an IChange<T>/ICreation<T> record, an AggregateRoot<T> or an IView<T, TSelf>");
            if ((kind is Kind.State or Kind.View) != (verb == "GET"))
                return Fail(kind is Kind.State or Kind.View ? "reads are GET endpoints" : "a change cannot be a GET endpoint");

            var members = new List<Member>();
            if (kind is Kind.State or Kind.View)
            {
                if (placeholders.Count != 1)
                    return Fail("a read needs exactly one route value: the id");
                members.Add(new Member(placeholders[0], $"global::Tarol.StronglyTypedId.Id<{aggregate}>", "id", false, true));
            }
            else
            {
                var ctor = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public
                        && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type)))
                    .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
                if (ctor is null)
                    return Fail("the input record needs a public primary constructor");
                foreach (var p in ctor.Parameters)
                {
                    ct.ThrowIfCancellationRequested();
                    var onRoute = placeholders.Any(h => string.Equals(h, p.Name, StringComparison.OrdinalIgnoreCase));
                    var t = p.Type.ToDisplayString(fq);
                    var routeKind = p.Type is INamedTypeSymbol { Name: "Id", TypeArguments.Length: 1 } && p.Type.ContainingNamespace.ToDisplayString() == "Tarol.StronglyTypedId" ? "id"
                        : p.Type.SpecialType == SpecialType.System_String ? "string"
                        : t == "global::System.Guid" ? "guid" : null;
                    if (onRoute && routeKind is null)
                        return Fail($"route member '{p.Name}' must be an Id<T>, a Guid or a string");
                    var optional = p.HasExplicitDefaultValue || p.NullableAnnotation == NullableAnnotation.Annotated
                                   || p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                    members.Add(new Member(char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1), t, routeKind, optional, onRoute));
                }
                foreach (var h in placeholders)
                    if (!members.Any(m => string.Equals(m.Name, h, StringComparison.OrdinalIgnoreCase)))
                        return Fail($"route value '{{{h}}}' is not a member of the input");
            }
            return new Endpoint(type.Name, full, kind, aggregate, verb, pattern, policy, anonymous, new Eq<Member>(members.ToImmutableArray()), null);
        }

        private static List<string> Placeholders(string pattern)
        {
            var list = new List<string>();
            for (var i = pattern.IndexOf('{'); i >= 0; i = pattern.IndexOf('{', i + 1))
            {
                var end = pattern.IndexOf('}', i);
                if (end < 0) break;
                var inner = pattern.Substring(i + 1, end - i - 1).TrimStart('*');
                var colon = inner.IndexOfAny(new[] { ':', '=', '?' });
                list.Add(colon < 0 ? inner : inner.Substring(0, colon));
            }
            return list;
        }
    }

    internal sealed record Hook(string Sender, string Type, string? Version, string Payload, string Change, string Method)
    {
        public static ImmutableArray<Hook> From(IMethodSymbol method, ImmutableArray<AttributeData> attributes)
        {
            var fq = SymbolDisplayFormat.FullyQualifiedFormat;
            if (!method.IsStatic || method.Parameters.Length != 1)
                return ImmutableArray<Hook>.Empty;
            return attributes.Select(a => new Hook((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!,
                a.NamedArguments.FirstOrDefault(n => n.Key == "Version").Value.Value as string,
                method.Parameters[0].Type.ToDisplayString(fq), method.ContainingType.ToDisplayString(fq), method.Name)).ToImmutableArray();
        }
    }

    private static string Emit(Endpoint[] endpoints, Hook[] hooks, Settings settings, string ns)
    {
        const string W = "global::Tarol.Changes.AspNetCore.ChangeEndpoints";
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n#nullable enable\nnamespace ").Append(ns).Append(";\n\n");
        sb.Append("/// <summary>The HTTP entries of this assembly's changes, reads and webhooks.</summary>\n");
        sb.Append("public static class GeneratedEndpoints\n{\n");
        sb.Append("    public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapChanges(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string? names = null)\n    {\n");
        foreach (var e in endpoints)
        {
            var body = e.Kind is Kind.Change or Kind.Creation && e.Members.Items.Any(m => !m.OnRoute);
            sb.Append("        global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapMethods(app, \"").Append(e.Pattern).Append("\", new[] { \"").Append(e.Verb)
              .Append("\" }, (global::Microsoft.AspNetCore.Http.RequestDelegate)").Append(Method(e)).Append(")\n");
            sb.Append("            .WithName(names + \"").Append(e.Name).Append(e.Kind == Kind.State ? "State" : "").Append("\")\n");
            if (e.Kind is Kind.Change or Kind.Creation)
                sb.Append("            .WithMetadata(new global::Microsoft.AspNetCore.Http.Metadata.AcceptsMetadata(new[] { \"application/json\" }, typeof(").Append(e.Full)
                  .Append("), isOptional: ").Append(body ? "false" : "true").Append("))\n");
            var produces = e.Kind switch { Kind.Creation => "201", Kind.Change => "204", _ => "200" };
            sb.Append("            .WithMetadata(new global::Microsoft.AspNetCore.Http.ProducesResponseTypeMetadata(").Append(produces)
              .Append(e.Kind == Kind.View ? $", typeof({e.Full}), new[] {{ \"application/json\" }}" : "").Append("))\n");
            if (settings.OpenApiMethod)
                sb.Append("            .WithMetadata(((global::Microsoft.AspNetCore.Http.RequestDelegate)").Append(Method(e)).Append(").Method)\n");
            sb.Append(e.Anonymous ? "            .AllowAnonymous();\n"
                : e.Policy is null ? "            .RequireAuthorization();                 // no Policy: the default policy (fail closed, C-11)\n"
                : $"            .RequireAuthorization(\"{e.Policy}\");\n");
        }
        foreach (var sender in hooks.Select(h => h.Sender).Distinct())
        {
            sb.Append("        global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPost(app, \"/webhooks/").Append(sender).Append("\", (global::Microsoft.AspNetCore.Http.RequestDelegate)Webhook_").Append(Ident(sender)).Append(").AllowAnonymous();\n");
            sb.Append("        global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapPost(app, \"/webhooks/").Append(sender).Append("/{tenant}\", (global::Microsoft.AspNetCore.Http.RequestDelegate)Webhook_").Append(Ident(sender)).Append(").AllowAnonymous();\n");
        }
        sb.Append("        return app;\n    }\n");

        foreach (var e in endpoints)
        {
            sb.Append("\n    private static async global::System.Threading.Tasks.Task ").Append(Method(e)).Append("(global::Microsoft.AspNetCore.Http.HttpContext http)\n    {\n");
            sb.Append("        var web = ").Append(W).Append(".Of(http);\n");
            foreach (var m in e.Members.Items.Where(m => m.OnRoute))
            {
                var raw = $"http.Request.RouteValues[\"{Camel(m.Name)}\"] as string";
                if (m.RouteKind == "string")
                    sb.Append("        var route_").Append(m.Name).Append(" = (").Append(raw).Append(") ?? \"\";\n");
                else
                    sb.Append("        if (!").Append(m.RouteKind == "guid" ? "global::System.Guid" : m.Type).Append(".TryParse(").Append(raw).Append(", null, out var route_").Append(m.Name).Append("))\n")
                      .Append("        {\n            await web.Problem(http, global::Tarol.Results.Invalid.Field(\"").Append(m.Name).Append("\", \"Format\"));\n            return;\n        }\n");
            }
            if (e.Kind is Kind.State or Kind.View)
            {
                var id = e.Members.Items[0].Name;
                sb.Append("        var aggregate = await web.Load<").Append(e.Aggregate).Append(">(http, route_").Append(id).Append(");\n");
                sb.Append("        if (aggregate is null)\n            return;\n");
                sb.Append(e.Kind == Kind.State ? "        await web.WriteState(http, aggregate);\n"
                    : $"        await web.WriteView(http, {e.Full}.From(aggregate));\n");
                sb.Append("    }\n");
                continue;
            }
            var hasBody = e.Members.Items.Any(m => !m.OnRoute);
            if (!hasBody)
                sb.Append("        var input = new ").Append(e.Full).Append("(").Append(string.Join(", ", e.Members.Items.Select(m => "route_" + m.Name))).Append(");\n");
            else if (settings.Binding == "record")
            {
                sb.Append("        var body = await web.ReadRecord<").Append(e.Full).Append(">(http);\n");
                sb.Append("        if (body.Failure is { } bad)\n        {\n            await web.Problem(http, bad);\n            return;\n        }\n");
                var with = string.Join(", ", e.Members.Items.Where(m => m.OnRoute).Select(m => $"{m.Name} = route_{m.Name}"));
                sb.Append("        var input = body.Value!").Append(with.Length > 0 ? $" with {{ {with} }}" : "").Append(";\n");
            }
            else
            {
                sb.Append("        var (bytes, status) = await web.ReadBytes(http);\n");
                sb.Append("        if (bytes is null)\n        {\n            http.Response.StatusCode = status;\n            return;\n        }\n");
                sb.Append("        var bad = Bind_").Append(e.Name).Append("(web, bytes").Append(string.Concat(e.Members.Items.Where(m => m.OnRoute).Select(m => ", route_" + m.Name))).Append(", out var input);\n");
                sb.Append("        if (bad is not null)\n        {\n            await web.Problem(http, bad);\n            return;\n        }\n");
            }
            sb.Append("        var (context, options, refused) = web.Edge(http);\n");
            sb.Append("        if (refused is not null)\n        {\n            await web.Problem(http, refused);\n            return;\n        }\n");
            sb.Append("        var result = await web.Pipeline.Run(input").Append(settings.Binding == "members" && hasBody ? "!" : "").Append(", context, options, http.RequestAborted);\n");
            sb.Append("        await web.Write(http, result, created: ").Append(e.Kind == Kind.Creation ? "true" : "false").Append(");\n    }\n");

            if (settings.Binding == "members" && hasBody)
                EmitBinder(sb, e);
        }

        foreach (var group in hooks.GroupBy(h => h.Sender))
        {
            sb.Append("\n    private static async global::System.Threading.Tasks.Task Webhook_").Append(Ident(group.Key)).Append("(global::Microsoft.AspNetCore.Http.HttpContext http)\n    {\n");
            sb.Append("        var web = ").Append(W).Append(".Of(http);\n");
            sb.Append("        var hooks = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Tarol.Changes.AspNetCore.Webhooks>(http.RequestServices);\n");
            sb.Append("        var delivery = await hooks.Read(http, web, \"").Append(group.Key).Append("\");\n");
            sb.Append("        if (delivery.Rejected != 0)\n        {\n            http.Response.StatusCode = delivery.Rejected;\n            return;\n        }\n");
            sb.Append("        var options = global::Tarol.Changes.AspNetCore.Webhooks.Options(delivery, \"").Append(group.Key).Append("\");\n");
            sb.Append("        global::Tarol.Results.Result<global::Tarol.Changes.ChangeOutcome> result;\n");
            sb.Append("        switch (delivery.Type, delivery.Version)\n        {\n");
            var i = 0;
            foreach (var h in group)
            {
                sb.Append("            case (\"").Append(h.Type).Append("\", ").Append(h.Version is null ? "null" : $"\"{h.Version}\"").Append("):\n            {\n");
                sb.Append("                ").Append(h.Payload).Append("? payload").Append(i).Append(";\n");
                sb.Append("                try { payload").Append(i).Append(" = global::System.Text.Json.JsonSerializer.Deserialize(delivery.Data, (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<").Append(h.Payload).Append(">)web.Json.GetTypeInfo(typeof(").Append(h.Payload).Append("))); }\n");
                sb.Append("                catch (global::System.Text.Json.JsonException) { payload").Append(i).Append(" = null; }\n");
                sb.Append("                if (payload").Append(i).Append(" is null)\n                {\n                    http.Response.StatusCode = 422;   // the payload does not match its contract: dead-letter at the sender\n                    return;\n                }\n");
                sb.Append("                result = await web.Pipeline.Run(").Append(h.Change).Append(".").Append(h.Method).Append("(payload").Append(i).Append("), delivery.Context!, options, http.RequestAborted);\n");
                sb.Append("                break;\n            }\n");
                i++;
            }
            var types = string.Join(" or ", group.Select(h => $"\"{h.Type}\"").Distinct());
            sb.Append("            case (").Append(types).Append(", _):\n                http.Response.StatusCode = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Microsoft.Extensions.Options.IOptionsMonitor<global::Tarol.Changes.AspNetCore.WebhookOptions>>(http.RequestServices).CurrentValue.UnknownVersionStatus;\n                return;\n");
            sb.Append("            default:\n                http.Response.StatusCode = 202;   // a type this app does not handle: acknowledge, no retry storm\n                return;\n        }\n");
            sb.Append("        await global::Tarol.Changes.AspNetCore.Webhooks.Answer(http, result);\n    }\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // Candidate B2: a member-by-member binder over Utf8JsonReader; the input is built with its constructor, every member
    // error is collected (Json = not valid for the member type, Required = absent and not optional).
    private static void EmitBinder(StringBuilder sb, Endpoint e)
    {
        var body = e.Members.Items.Where(m => !m.OnRoute).ToArray();
        sb.Append("\n    private static string[]? names_").Append(e.Name).Append(";\n\n");
        sb.Append("    private static global::Tarol.Results.Invalid? Bind_").Append(e.Name).Append("(global::Tarol.Changes.AspNetCore.ChangeEndpoints web, byte[] bytes");
        foreach (var m in e.Members.Items.Where(m => m.OnRoute))
            sb.Append(", ").Append(m.RouteKind == "guid" ? "global::System.Guid" : m.RouteKind == "string" ? "string" : m.Type).Append(" route_").Append(m.Name);
        sb.Append(", out ").Append(e.Full).Append("? input)\n    {\n");
        sb.Append("        var names = names_").Append(e.Name).Append(" ??= web.Names(").Append(string.Join(", ", body.Select(m => $"\"{m.Name}\""))).Append(");\n");
        sb.Append("        input = null;\n        global::System.Collections.Generic.List<global::Tarol.Results.FieldError>? errors = null;\n");
        foreach (var m in body)
            sb.Append("        ").Append(m.Type).Append(" v_").Append(m.Name).Append(" = default!;\n        var has_").Append(m.Name).Append(" = false;\n");
        sb.Append("        var reader = new global::System.Text.Json.Utf8JsonReader(bytes);\n");
        sb.Append("        try\n        {\n");
        sb.Append("            if (!reader.Read() || reader.TokenType != global::System.Text.Json.JsonTokenType.StartObject)\n                return global::Tarol.Results.Invalid.Field(\"\", \"Json\");\n");
        sb.Append("            while (reader.Read() && reader.TokenType == global::System.Text.Json.JsonTokenType.PropertyName)\n            {\n");
        for (var i = 0; i < body.Length; i++)
        {
            var m = body[i];
            sb.Append("                ").Append(i == 0 ? "if" : "else if").Append(" (web.Is(ref reader, names[").Append(i).Append("]))\n                {\n");
            sb.Append("                    reader.Read();\n");
            sb.Append("                    if (web.TryValue<").Append(m.Type).Append(">(ref reader, out v_").Append(m.Name).Append("!))\n                        has_").Append(m.Name).Append(" = true;\n");
            sb.Append("                    else\n                    {\n                        (errors ??= []).Add(new(\"").Append(m.Name).Append("\", \"Json\"));\n                        reader.Skip();\n                    }\n                }\n");
        }
        sb.Append(body.Length > 0 ? "                else\n                {\n                    reader.Read();\n                    reader.Skip();\n                }\n" : "                reader.Read(); reader.Skip();\n");
        sb.Append("            }\n        }\n        catch (global::System.Text.Json.JsonException)\n        {\n            return global::Tarol.Results.Invalid.Field(\"\", \"Json\");\n        }\n");
        foreach (var m in body.Where(m => !m.Optional))
            sb.Append("        if (!has_").Append(m.Name).Append(" && errors?.Exists(f => f.Field == \"").Append(m.Name).Append("\") != true)\n            (errors ??= []).Add(new(\"").Append(m.Name).Append("\", \"Required\"));\n");
        sb.Append("        if (errors is not null)\n            return new global::Tarol.Results.Invalid(errors);\n");
        sb.Append("        input = new ").Append(e.Full).Append("(").Append(string.Join(", ", e.Members.Items.Select(m => m.OnRoute ? "route_" + m.Name : "v_" + m.Name))).Append(");\n");
        sb.Append("        return null;\n    }\n");
    }

    private static string Method(Endpoint e) => (e.Kind switch { Kind.State => "Get_", Kind.View => "View_", _ => "Run_" }) + e.Name;
    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    private static string Ident(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>Value equality for generator models (ImmutableArray compares by reference).</summary>
internal readonly struct Eq<T> : IEquatable<Eq<T>>, IEnumerable<T>
{
    public static Eq<T> Empty => new(ImmutableArray<T>.Empty);
    public Eq(ImmutableArray<T> items) => Items = items;
    public ImmutableArray<T> Items { get; }
    public bool Equals(Eq<T> other) => Items.SequenceEqual(other.Items);
    public override bool Equals(object? obj) => obj is Eq<T> o && Equals(o);
    public override int GetHashCode() => Items.Aggregate(17, (h, x) => h * 31 + (x?.GetHashCode() ?? 0));
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
