using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tarol.Validations.Generators;

/// <summary>
///     Generates validation for two kinds of records:
///     <list type="bullet">
///         <item>
///             <b>Value objects</b>: a record or record struct whose primary constructor has a Tarol rule attribute
///             (<c>[Range]</c>, <c>[Length]</c>, …) or that declares a custom rule <c>static void Validate(…, ref ValidationErrors)</c>.
///             It gets <c>Create(…) : Result&lt;T&gt;</c>, <c>Check(T, ref ValidationErrors)</c>, limit constants and, with one
///             field, a conversion to the field, comparisons, <c>ToString</c> and a trusting JSON converter.
///         </item>
///         <item><b>Inputs</b>: a record that declares <c>IValidatable</c>. It gets <c>Validate(ref ValidationErrors)</c>.</item>
///     </list>
///     The transform produces the finished source text and the diagnostics (strings and value types only), so each type is
///     cached on its own; there is no <c>Collect()</c> across types (finding 12).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ValidationGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is RecordDeclarationSyntax record && (record.ParameterList is not null || record.BaseList is not null),
                static (ctx, ct) => Transform(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(types, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (model.Source is not null)
                spc.AddSource(model.HintName, SourceText.From(model.Source, Encoding.UTF8));
        });
    }

    private static TypeModel? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var node = (RecordDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(node, ct) is not { } type)
            return null;

        // One model per type: the declaration with the parameter list, else the first declaration.
        var primary = type.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .OfType<RecordDeclarationSyntax>()
            .OrderBy(static r => r.ParameterList is null ? 1 : 0)
            .FirstOrDefault();
        if (primary != node)
            return null;

        var isInput = type.Interfaces.Any(Symbols.IsValidatableInterface);
        var constructor = Symbols.PrimaryConstructor(type);
        var single = constructor is { Parameters.Length: 1 } ? constructor.Parameters[0].Type : null;
        var hasRules = constructor is not null && constructor.Parameters.Any(static p => p.GetAttributes().Any(Symbols.IsRule));
        var custom = isInput ? null : Symbols.CustomRule(type, single);
        var isValueObject = !isInput && (hasRules || custom is not null);

        if (!isInput && !isValueObject)
            return null;

        // A hand-written IValidatable.Validate wins; the generator stays out of the way.
        if (isInput && type.GetMembers("Validate").OfType<IMethodSymbol>().Any(static m =>
                !m.IsStatic && m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.Ref && Symbols.IsValidationErrors(m.Parameters[0].Type)))
            return null;

        var emitter = new Emitter(type, ct);
        var hint = HintName(type);

        if (!emitter.CheckShape(isInput))
            return new TypeModel(hint, null, emitter.Diagnostics);

        var source = isInput ? emitter.EmitInput(constructor) : emitter.EmitValueObject(constructor!, custom);
        return new TypeModel(hint, emitter.HasErrors ? null : source, emitter.Diagnostics);
    }

    private static string HintName(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var sb = new StringBuilder(name.Length + 16);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' ? ch : '_');
        return sb.Append(".Validation.g.cs").ToString();
    }
}

/// <summary>The cached result of the transform: the finished source (or none) and the diagnostics.</summary>
internal sealed record TypeModel(string HintName, string? Source, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>An equatable diagnostic for the pipeline (no <see cref="Location" /> or symbols in models).</summary>
internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public Diagnostic ToDiagnostic()
    {
        var args = new object[Arguments.Count];
        var i = 0;
        foreach (var argument in Arguments)
            args[i++] = argument;
        return Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(), args);
    }
}

internal static class SyntaxNames
{
    /// <summary>An identifier as an expression (<c>@class</c> for keywords).</summary>
    public static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) is SyntaxKind.FieldKeyword
            ? "@" + name
            : name;

    /// <summary>The camelCase form of a member name, for parameters (<c>Value</c> → <c>value</c>); never <c>created</c> or <c>errors</c>.</summary>
    public static string Camel(string name)
    {
        var camel = name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        return camel is "created" or "errors" or "invalid" ? camel + "Value" : camel;
    }

    public static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    public static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
