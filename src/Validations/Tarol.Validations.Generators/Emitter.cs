using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarol.Validations.Generators;

/// <summary>Analyses one value object or input and writes its partial declaration.</summary>
internal sealed class Emitter
{
    private const string Errors = "global::Tarol.Validations.ValidationErrors";
    private const string RulesType = "global::Tarol.Validations.Rules";

    private readonly INamedTypeSymbol _type;
    private readonly CancellationToken _ct;
    private readonly ImmutableArray<DiagnosticInfo>.Builder _diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
    private readonly List<string> _fields = [];
    private readonly List<string> _constants = [];
    private int _temp;

    public Emitter(INamedTypeSymbol type, CancellationToken ct)
    {
        _type = type;
        _ct = ct;
    }

    public EquatableArray<DiagnosticInfo> Diagnostics => new(_diagnostics.ToImmutable());

    public bool HasErrors => _diagnostics.Any(static d => d.Id is not "TVAL008");

    private string TypeName => _type.Name;

    // ---------------------------------------------------------------- shape

    /// <summary>TVAL001 (not partial, also containing types) and TVAL002 (unsupported shapes).</summary>
    public bool CheckShape(bool isInput)
    {
        var location = _type.Locations.FirstOrDefault();

        for (var current = _type; current is not null; current = current.ContainingType)
        {
            var allPartial = current.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(_ct))
                .OfType<TypeDeclarationSyntax>()
                .All(static d => d.Modifiers.Any(static m => m.ValueText == "partial"));
            if (!allPartial)
            {
                var detail = SymbolEqualityComparer.Default.Equals(current, _type) ? string.Empty : $" (it contains '{_type.Name}')";
                Report("TVAL001", current.Locations.FirstOrDefault() ?? location, current.Name, detail);
                return false;
            }

            if (current.IsGenericType)
            {
                Report("TVAL002", location, _type.Name, "generic types (and types nested in generic types) are not supported");
                return false;
            }

            if (current.IsFileLocal)
            {
                Report("TVAL002", location, _type.Name, "file-local types cannot be extended by generated code");
                return false;
            }
        }

        if (isInput && _type.BaseType is { } baseType && Symbols.ImplementsValidatable(baseType))
        {
            Report("TVAL002", location, _type.Name, $"its base '{baseType.Name}' is already IValidatable; implement Validate by hand or do not derive inputs");
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- inputs

    public string EmitInput(IMethodSymbol? constructor)
    {
        var body = new StringBuilder();
        if (constructor is not null)
        {
            foreach (var parameter in constructor.Parameters)
            {
                _ct.ThrowIfCancellationRequested();
                EmitMember(body, "        ", "this." + SyntaxNames.Identifier(parameter.Name), parameter, enter: true, isInput: true);
            }
        }

        var members = new StringBuilder();
        members.Append("    /// <inheritdoc cref=\"global::Tarol.Validations.IValidatable.Validate\" />\n");
        members.Append("    public void Validate(ref ").Append(Errors).Append(" errors)\n    {\n");
        members.Append(body);
        members.Append("    }\n");
        return Wrap(members.ToString(), attributes: null);
    }

    // ---------------------------------------------------------------- value objects

    public string EmitValueObject(IMethodSymbol constructor, IMethodSymbol? custom)
    {
        var self = SyntaxNames.FullName(_type);
        var parameters = constructor.Parameters;
        var single = parameters.Length == 1 ? parameters[0] : null;
        var isStruct = _type.IsValueType;

        var check = new StringBuilder();
        if (!isStruct)
            check.Append("        if (value is null)\n        {\n            errors.Add(\"Required\");\n            return;\n        }\n\n");

        if (custom is not null)
            check.Append("        var before = errors.Count;\n");

        foreach (var parameter in parameters)
        {
            _ct.ThrowIfCancellationRequested();
            EmitMember(check, "        ", "value." + SyntaxNames.Identifier(parameter.Name), parameter, enter: single is null, isInput: false);
        }

        if (custom is not null)
        {
            var argument = SymbolEqualityComparer.Default.Equals(custom.Parameters[0].Type, _type)
                ? "value"
                : "value." + SyntaxNames.Identifier(single!.Name);
            check.Append("        if (errors.Count == before)\n            Validate(").Append(argument).Append(", ref errors);\n");
        }

        var sb = new StringBuilder();
        foreach (var constant in _constants)
            sb.Append(constant);
        if (_constants.Count > 0)
            sb.Append('\n');

        var parameterList = string.Join(", ", parameters.Select(static p => SyntaxNames.FullName(p.Type) + " " + SyntaxNames.Identifier(SyntaxNames.Camel(p.Name))));
        var argumentList = string.Join(", ", parameters.Select(static p => SyntaxNames.Identifier(SyntaxNames.Camel(p.Name))));

        if (!HasMember("Create"))
        {
            sb.Append("    /// <summary>Creates a <see cref=\"").Append(TypeName).Append("\" /> when every rule holds; otherwise the <see cref=\"global::Tarol.Results.Invalid\" /> with all field errors.</summary>\n");
            foreach (var p in parameters)
                sb.Append("    /// <param name=\"").Append(SyntaxNames.Camel(p.Name)).Append("\">The <c>").Append(p.Name).Append("</c>.</param>\n");
            sb.Append("    /// <returns>The value object, or <see cref=\"global::Tarol.Results.Invalid\" />.</returns>\n");
            sb.Append("    public static global::Tarol.Results.Result<").Append(self).Append("> Create(").Append(parameterList).Append(")\n    {\n");
            sb.Append("        var created = new ").Append(self).Append('(').Append(argumentList).Append(");\n");
            sb.Append("        var errors = default(").Append(Errors).Append(");\n");
            sb.Append("        Check(created, ref errors);\n");
            sb.Append("        return errors.ToInvalid() is { } invalid ? invalid : created;\n    }\n\n");
        }

        if (!HasMember("Check"))
        {
            sb.Append("    /// <summary>Adds an error to <paramref name=\"errors\" /> for every rule <paramref name=\"value\" /> breaks, at the current path.</summary>\n");
            sb.Append("    /// <param name=\"value\">The value to check (it may come from deserialization, which does not validate).</param>\n");
            sb.Append("    /// <param name=\"errors\">The collector.</param>\n");
            sb.Append("    public static void Check(").Append(self).Append(" value, ref ").Append(Errors).Append(" errors)\n    {\n");
            sb.Append(check);
            sb.Append("    }\n");
        }

        string? attributes = null;
        var interfaces = new List<string>();
        if (single is not null)
            attributes = EmitSingleFieldMembers(sb, single, isStruct, interfaces);

        foreach (var field in _fields)
            sb.Append('\n').Append(field);

        return Wrap(sb.ToString(), attributes, interfaces);
    }

    private string? EmitSingleFieldMembers(StringBuilder sb, IParameterSymbol field, bool isStruct, List<string> interfaces)
    {
        var self = SyntaxNames.FullName(_type);
        var fieldType = field.Type;
        var fieldName = SyntaxNames.FullName(fieldType);
        var access = "value." + SyntaxNames.Identifier(field.Name);
        var thisAccess = "this." + SyntaxNames.Identifier(field.Name);

        if (isStruct && !HasConversionTo(fieldType))
        {
            sb.Append("\n    /// <summary>The underlying value: arithmetic and comparisons with <see cref=\"").Append(EscapeXml(fieldType.ToDisplayString())).Append("\" /> work on it.</summary>\n");
            sb.Append("    /// <param name=\"value\">The value object.</param>\n");
            sb.Append("    public static implicit operator ").Append(fieldName).Append('(').Append(self).Append(" value) => ").Append(access).Append(";\n");
        }

        if (isStruct && fieldType.SpecialType != SpecialType.System_String && IsComparable(fieldType) && !HasMember("CompareTo"))
        {
            interfaces.Add("global::System.IComparable<" + self + ">");
            sb.Append("\n    /// <inheritdoc />\n");
            sb.Append("    public int CompareTo(").Append(self).Append(" other) => ").Append(thisAccess).Append(".CompareTo(other.").Append(SyntaxNames.Identifier(field.Name)).Append(");\n");
            foreach (var op in new[] { "<", ">", "<=", ">=" })
            {
                sb.Append("\n    /// <summary>Compares the underlying values.</summary>\n");
                sb.Append("    /// <param name=\"left\">The left value.</param>\n    /// <param name=\"right\">The right value.</param>\n");
                sb.Append("    /// <returns>The comparison of the underlying values.</returns>\n");
                sb.Append("    public static bool operator ").Append(op).Append('(').Append(self).Append(" left, ").Append(self)
                    .Append(" right) => left.CompareTo(right) ").Append(op).Append(" 0;\n");
            }
        }

        if (!HasUserMember("ToString"))
        {
            var text = fieldType.SpecialType == SpecialType.System_String
                ? thisAccess + " ?? string.Empty"
                : IsFormattable(fieldType)
                    ? thisAccess + ".ToString(null, global::System.Globalization.CultureInfo.InvariantCulture)"
                    : fieldType.IsValueType ? thisAccess + ".ToString()" : thisAccess + "?.ToString() ?? string.Empty";
            sb.Append("\n    /// <summary>The underlying value as text (invariant culture).</summary>\n");
            sb.Append("    /// <returns>The text.</returns>\n");
            sb.Append("    public override string ToString() => ").Append(text).Append(";\n");
        }

        if (JsonAccess(fieldType) is not { } json || HasJsonConverterAttribute())
            return null;

        var converter = self + ".ValueJsonConverter";
        sb.Append("\n    /// <summary>\n");
        sb.Append("    ///     Writes the bare underlying value and reads it back <b>without validation</b>: old events and snapshots must load even\n");
        sb.Append("    ///     when today's rules are stricter. Validate inputs with <c>IValidatable</c> or <c>Create</c>.\n");
        sb.Append("    /// </summary>\n");
        sb.Append("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append("    public sealed class ValueJsonConverter : global::System.Text.Json.Serialization.JsonConverter<").Append(self).Append(">\n    {\n");
        sb.Append("        /// <inheritdoc />\n");
        sb.Append("        public override ").Append(self).Append(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)\n        {\n");
        if (isStruct)
            sb.Append("            if (reader.TokenType == global::System.Text.Json.JsonTokenType.Null)\n                return default;\n\n");
        sb.Append("            return new ").Append(self).Append("(reader.").Append(json.Read).Append("()").Append(json.Read == "GetString" ? "!" : string.Empty).Append(");\n        }\n\n");
        sb.Append("        /// <inheritdoc />\n");
        sb.Append("        public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ").Append(self).Append(" value, global::System.Text.Json.JsonSerializerOptions options)\n        {\n");
        sb.Append("            global::System.ArgumentNullException.ThrowIfNull(writer);\n");
        if (fieldType.SpecialType == SpecialType.System_String)
            sb.Append("            if (").Append(access).Append(" is null)\n            {\n                writer.WriteNullValue();\n                return;\n            }\n\n");
        sb.Append("            writer.").Append(json.Write).Append('(').Append(json.Cast).Append(access).Append(");\n        }\n    }\n");

        if (IsModuleVisible(_type))
        {
            sb.Append("\n    /// <summary>Registers <see cref=\"ValueJsonConverter\" /> with <c>ValueObjectJson.Resolver</c> for source-generated contexts in this assembly.</summary>\n");
            sb.Append("    [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
            sb.Append("    internal static void RegisterValueJsonConverter() => global::Tarol.Validations.ValueObjectJson.Register(new ValueJsonConverter());\n");
        }

        return "[global::System.Text.Json.Serialization.JsonConverter(typeof(" + converter + "))]";
    }

    // ---------------------------------------------------------------- members

    /// <summary>Emits the checks of one member (input parameter or value-object field).</summary>
    private void EmitMember(StringBuilder sb, string indent, string expression, IParameterSymbol parameter, bool enter, bool isInput)
    {
        var rules = parameter.GetAttributes().Where(Symbols.IsRule).ToImmutableArray();
        var location = parameter.Locations.FirstOrDefault();
        var body = new StringBuilder();
        EmitValue(body, indent + (enter ? "    " : string.Empty), expression, parameter.Type, parameter.NullableAnnotation, rules,
            parameter.Name, location, isInput, depth: 0, singleField: !enter && !isInput);

        if (body.Length == 0)
            return;

        if (enter)
        {
            sb.Append(indent).Append("errors.Enter(").Append(SyntaxNames.Literal(parameter.Name)).Append(");\n");
            sb.Append(body);
            sb.Append(indent).Append("errors.Exit();\n");
        }
        else
        {
            sb.Append(body);
        }
    }

    /// <summary>Unwraps <c>Option&lt;T&gt;</c>, <c>T?</c> and the required check, then emits the core checks.</summary>
    private void EmitValue(StringBuilder sb, string indent, string expression, ITypeSymbol type, NullableAnnotation annotation,
        ImmutableArray<AttributeData> rules, string member, Location? location, bool isInput, int depth, bool singleField)
    {
        if (Symbols.IsOption(type, out var inner))
        {
            var variable = Temp();
            var core = new StringBuilder();
            EmitCore(core, indent + "    ", variable, inner, rules, member, location, isInput, depth, singleField);
            if (core.Length > 0)
                sb.Append(indent).Append("if (").Append(expression).Append(" is ").Append(SyntaxNames.FullName(inner)).Append(' ').Append(variable).Append(")\n")
                    .Append(indent).Append("{\n").Append(core).Append(indent).Append("}\n");
            return;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var variable = Temp();
            var core = new StringBuilder();
            EmitCore(core, indent + "    ", variable, nullable.TypeArguments[0], rules, member, location, isInput, depth, singleField);
            if (core.Length > 0)
                sb.Append(indent).Append("if (").Append(expression).Append(" is { } ").Append(variable).Append(")\n")
                    .Append(indent).Append("{\n").Append(core).Append(indent).Append("}\n");
            return;
        }

        if (type.IsReferenceType)
        {
            var variable = Temp();
            var core = new StringBuilder();
            EmitCore(core, indent + "    ", variable, type, rules, member, location, isInput, depth, singleField);
            var optional = annotation == NullableAnnotation.Annotated;
            if (optional && core.Length == 0)
                return;

            sb.Append(indent).Append("if (").Append(expression).Append(" is { } ").Append(variable).Append(")\n")
                .Append(indent).Append("{\n").Append(core).Append(indent).Append("}\n");
            if (!optional)
                sb.Append(indent).Append("else\n").Append(indent).Append("{\n").Append(indent).Append("    errors.Add(\"Required\");\n").Append(indent).Append("}\n");
            return;
        }

        EmitCore(sb, indent, expression, type, rules, member, location, isInput, depth, singleField);
    }

    /// <summary>The checks of a non-null value.</summary>
    private void EmitCore(StringBuilder sb, string indent, string value, ITypeSymbol type, ImmutableArray<AttributeData> rules,
        string member, Location? location, bool isInput, int depth, bool singleField)
    {
        var typeText = type.ToDisplayString();

        if (Symbols.IsValueObject(type) && !SymbolEqualityComparer.Default.Equals(type, _type))
        {
            RejectRules(rules, member, typeText, $"'{type.Name}' is a value object; its rules are declared on '{type.Name}'");
            sb.Append(indent).Append(SyntaxNames.FullName(type)).Append(".Check(").Append(value).Append(", ref errors);\n");
            return;
        }

        if (Symbols.ImplementsValidatable(type))
        {
            RejectRules(rules, member, typeText, $"'{type.Name}' is IValidatable; its rules are declared on '{type.Name}'");
            sb.Append(indent).Append(value).Append(".Validate(ref errors);\n");
            return;
        }

        if (Symbols.IsId(type))
        {
            RejectRules(rules.Where(static r => Symbols.RuleName(r) != "NotEmpty").ToImmutableArray(), member, typeText, "an id only takes [NotEmpty] (and is required anyway)");
            sb.Append(indent).Append("if (").Append(value).Append(".Value == global::System.Guid.Empty)\n").Append(indent).Append("    errors.Add(\"Required\");\n");
            return;
        }

        if (Collection(type, out var element, out var countProperty))
        {
            EmitCollection(sb, indent, value, element!, countProperty!, rules, member, typeText, location, isInput, depth);
            return;
        }

        if (!IsKnownLeaf(type))
        {
            RejectRules(rules, member, typeText, "rules apply to numbers, strings, Guid, ids and collections");
            if (rules.IsEmpty)
                Report("TVAL008", location, member, _type.Name, typeText);
            return;
        }

        EmitRules(sb, indent, value, type, rules, member, typeText, singleField);
    }

    private void EmitCollection(StringBuilder sb, string indent, string value, ITypeSymbol element, string countProperty,
        ImmutableArray<AttributeData> rules, string member, string typeText, Location? location, bool isInput, int depth)
    {
        var chain = new List<RuleCheck>();
        foreach (var rule in rules)
        {
            if (Symbols.RuleName(rule) != "Count")
            {
                RejectRules(ImmutableArray.Create(rule), member, typeText, "a collection only takes [Count]; element rules belong on the element type");
                continue;
            }

            if (IntBounds(rule, member, out var min, out var max))
                chain.Add(BoundsCheck(value + "." + countProperty, min, max, "Count"));
        }

        AppendChain(sb, indent, chain);

        if (element is IArrayTypeSymbol || (Collection(element, out _, out _) && element.SpecialType != SpecialType.System_String))
        {
            Report("TVAL008", location, member, _type.Name, typeText);
            return;
        }

        var index = Temp();
        var body = new StringBuilder();
        var annotation = element.NullableAnnotation;
        EmitValue(body, indent + "    ", value + "[" + index + "]", element, annotation, ImmutableArray<AttributeData>.Empty,
            member, location, isInput, depth + 1, singleField: false);
        if (body.Length == 0)
            return;

        sb.Append(indent).Append("for (var ").Append(index).Append(" = 0; ").Append(index).Append(" < ").Append(value).Append('.').Append(countProperty)
            .Append("; ").Append(index).Append("++)\n").Append(indent).Append("{\n");
        sb.Append(indent).Append("    errors.EnterIndex(").Append(index).Append(");\n");
        sb.Append(body);
        sb.Append(indent).Append("    errors.Exit();\n");
        sb.Append(indent).Append("}\n");
    }

    // ---------------------------------------------------------------- rules on leaves

    private void EmitRules(StringBuilder sb, string indent, string value, ITypeSymbol type, ImmutableArray<AttributeData> rules,
        string member, string typeText, bool singleField)
    {
        var chain = new List<RuleCheck>();
        var isString = type.SpecialType == SpecialType.System_String;
        var numeric = NumericKind(type);

        foreach (var rule in rules)
        {
            var name = Symbols.RuleName(rule)!;
            switch (name)
            {
                case "Range" when numeric is not null:
                    if (RangeBounds(rule, member, type, numeric, out var minLiteral, out var maxLiteral, out var detail))
                    {
                        var parts = new List<string>();
                        if (minLiteral is not null)
                            parts.Add(value + " >= " + minLiteral);
                        if (maxLiteral is not null)
                            parts.Add(value + " <= " + maxLiteral);
                        if (parts.Count > 0)
                            chain.Add(new RuleCheck("!(" + string.Join(" && ", parts) + ")", "Range", detail));
                        AddConstants(member, singleField, "", type, minLiteral, maxLiteral);
                    }

                    break;
                case "Length" when isString:
                    if (IntBounds(rule, member, out var min, out var max))
                    {
                        chain.Add(BoundsCheck(value + ".Length", min, max, "Length"));
                        AddConstants(member, singleField, "Length", type: null,
                            min > 0 ? min.ToString(CultureInfo.InvariantCulture) : null,
                            max < int.MaxValue ? max.ToString(CultureInfo.InvariantCulture) : null);
                    }

                    break;
                case "Email" when isString:
                    chain.Add(new RuleCheck("!" + RulesType + ".Email(" + value + ")", "Email", ""));
                    break;
                case "Pattern" when isString:
                    if (PatternField(rule, member) is { } field)
                        chain.Add(new RuleCheck("!" + RulesType + ".Matches(" + field + ", " + value + ")", "Pattern", ""));
                    break;
                case "NotEmpty" when isString:
                    chain.Insert(0, new RuleCheck("!" + RulesType + ".NotBlank(" + value + ")", "Required", ""));
                    break;
                case "NotEmpty" when type.SpecialType == SpecialType.None && type.ToDisplayString() == "System.Guid":
                    chain.Insert(0, new RuleCheck(value + " == global::System.Guid.Empty", "Required", ""));
                    break;
                default:
                    Report("TVAL004", Where(rule), name, member, typeText, Applicability(name));
                    break;
            }
        }

        AppendChain(sb, indent, chain);
    }

    /// <summary>One error per value: the first failing rule wins (finding 4: no duplicates).</summary>
    private static void AppendChain(StringBuilder sb, string indent, List<RuleCheck> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var check = chain[i];
            sb.Append(indent).Append(i == 0 ? "if (" : "else if (").Append(check.Condition).Append(")\n");
            sb.Append(indent).Append("    errors.Add(").Append(SyntaxNames.Literal(check.Code));
            if (check.Detail.Length > 0)
                sb.Append(", ").Append(SyntaxNames.Literal(check.Detail));
            sb.Append(");\n");
        }
    }

    private sealed record RuleCheck(string Condition, string Code, string Detail);

    private static RuleCheck BoundsCheck(string expression, int min, int max, string code)
    {
        var parts = new List<string>();
        if (min > 0)
            parts.Add(expression + " < " + min.ToString(CultureInfo.InvariantCulture));
        if (max < int.MaxValue)
            parts.Add(expression + " > " + max.ToString(CultureInfo.InvariantCulture));
        var detail = (min > 0 ? min.ToString(CultureInfo.InvariantCulture) : string.Empty) + ".." +
                     (max < int.MaxValue ? max.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return new RuleCheck(parts.Count == 0 ? "false" : string.Join(" || ", parts), code, detail);
    }

    private bool IntBounds(AttributeData rule, string member, out int min, out int max)
    {
        min = 0;
        max = int.MaxValue;
        if (rule.ConstructorArguments.Length == 2)
        {
            min = (int)rule.ConstructorArguments[0].Value!;
            max = (int)rule.ConstructorArguments[1].Value!;
        }

        foreach (var named in rule.NamedArguments)
        {
            if (named.Key == "Min" && named.Value.Value is int namedMin)
                min = namedMin;
            else if (named.Key == "Max" && named.Value.Value is int namedMax)
                max = namedMax;
        }

        var name = Symbols.RuleName(rule)!;
        if (min < 0 || max < 0)
            Report("TVAL005", Where(rule), name, member, "Min and Max must not be negative");
        else if (min > max)
            Report("TVAL005", Where(rule), name, member, $"Min ({min}) is greater than Max ({max})");
        else
            return true;

        return false;
    }

    private bool RangeBounds(AttributeData rule, string member, ITypeSymbol type, string numeric,
        out string? minLiteral, out string? maxLiteral, out string detail)
    {
        minLiteral = maxLiteral = null;
        detail = string.Empty;
        TypedConstant? min = null, max = null;
        if (rule.ConstructorArguments.Length == 2)
        {
            min = rule.ConstructorArguments[0];
            max = rule.ConstructorArguments[1];
        }

        foreach (var named in rule.NamedArguments)
        {
            if (named.Key == "Min")
                min = named.Value;
            else if (named.Key == "Max")
                max = named.Value;
        }

        if (min is null && max is null)
        {
            Report("TVAL005", Where(rule), "Range", member, "set Min, Max or both");
            return false;
        }

        if (!Bound(min, numeric, out var minValue, out var minText, out var minError)
            || !Bound(max, numeric, out var maxValue, out var maxText, out minError))
        {
            Report("TVAL005", Where(rule), "Range", member, minError!);
            return false;
        }

        if (minValue is { } lo && maxValue is { } hi && lo > hi)
        {
            Report("TVAL005", Where(rule), "Range", member, $"Min ({minText}) is greater than Max ({maxText})");
            return false;
        }

        minLiteral = minText is null ? null : Literal(minText, numeric);
        maxLiteral = maxText is null ? null : Literal(maxText, numeric);
        detail = (minText ?? string.Empty) + ".." + (maxText ?? string.Empty);
        _ = type;
        return true;
    }

    /// <summary>Converts a bound to the target number type, as invariant text; checks that the type can hold it.</summary>
    private static bool Bound(TypedConstant? constant, string numeric, out decimal? value, out string? text, out string? error)
    {
        value = null;
        text = null;
        error = null;
        if (constant is not { } c || c.IsNull)
            return true;

        decimal parsed;
        switch (c.Value)
        {
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed):
                break;
            case string s:
                error = $"'{s}' is not an invariant number";
                return false;
            case double d when numeric is "double" or "float":
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    error = "a bound must be a finite number";
                    return false;
                }

                value = d >= (double)decimal.MaxValue ? decimal.MaxValue : d <= (double)decimal.MinValue ? decimal.MinValue : (decimal)d;
                text = d.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d) || d > (double)decimal.MaxValue || d < (double)decimal.MinValue)
                {
                    error = $"{d.ToString("R", CultureInfo.InvariantCulture)} does not fit '{numeric}'";
                    return false;
                }

                parsed = (decimal)d;
                break;
            case IConvertible convertible:
                parsed = convertible.ToDecimal(CultureInfo.InvariantCulture);
                break;
            default:
                error = "a bound must be a number or an invariant number string";
                return false;
        }

        var (lo, hi, integral) = Limits(numeric);
        if (parsed < lo || parsed > hi)
        {
            error = $"{parsed.ToString(CultureInfo.InvariantCulture)} does not fit '{numeric}'";
            return false;
        }

        if (integral && decimal.Truncate(parsed) != parsed)
        {
            error = $"{parsed.ToString(CultureInfo.InvariantCulture)} is not a whole number, but '{numeric}' is";
            return false;
        }

        value = parsed;
        text = parsed.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static (decimal Low, decimal High, bool Integral) Limits(string numeric) => numeric switch
    {
        "byte" => (byte.MinValue, byte.MaxValue, true),
        "sbyte" => (sbyte.MinValue, sbyte.MaxValue, true),
        "short" => (short.MinValue, short.MaxValue, true),
        "ushort" => (ushort.MinValue, ushort.MaxValue, true),
        "int" => (int.MinValue, int.MaxValue, true),
        "uint" => (uint.MinValue, uint.MaxValue, true),
        "long" => (long.MinValue, long.MaxValue, true),
        "ulong" => (ulong.MinValue, ulong.MaxValue, true),
        _ => (decimal.MinValue, decimal.MaxValue, false)
    };

    private static string Literal(string text, string numeric) => numeric switch
    {
        "int" => text,
        "long" => text + "L",
        "uint" => text + "U",
        "ulong" => text + "UL",
        "byte" or "sbyte" or "short" or "ushort" => "(" + numeric + ")" + text,
        "float" => text + "F",
        "double" => text + "D",
        _ => text + "M"
    };

    private static string? NumericKind(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte => "byte",
        SpecialType.System_SByte => "sbyte",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_Decimal => "decimal",
        _ => null
    };

    private void AddConstants(string member, bool singleField, string suffix, ITypeSymbol? type, string? min, string? max)
    {
        // Constants only on value objects; with one field they are plain (Min, MaxLength), otherwise prefixed (AmountMin).
        if (!Symbols.IsValueObject(_type) && Symbols.ImplementsValidatable(_type))
            return;

        var prefix = singleField ? string.Empty : member;
        var constType = type is null ? "int" : type.ToDisplayString();
        void Add(string name, string literal, string what)
        {
            if (HasMember(name) || _constants.Any(c => c.Contains(" " + name + " =")))
                return;
            _constants.Add("    /// <summary>The " + what + " of " + (singleField ? "the value" : "<c>" + member + "</c>") + " (inclusive).</summary>\n" +
                           "    public const " + constType + " " + name + " = " + literal + ";\n");
        }

        if (min is not null)
            Add(prefix + "Min" + suffix, min, suffix.Length == 0 ? "lower bound" : "minimum length");
        if (max is not null)
            Add(prefix + "Max" + suffix, max, suffix.Length == 0 ? "upper bound" : "maximum length");
    }

    private string? PatternField(AttributeData rule, string member)
    {
        var pattern = rule.ConstructorArguments.Length == 1 ? rule.ConstructorArguments[0].Value as string : null;
        var timeout = 100;
        var ignoreCase = false;
        foreach (var named in rule.NamedArguments)
        {
            if (named.Key == "TimeoutMilliseconds" && named.Value.Value is int t)
                timeout = t;
            else if (named.Key == "IgnoreCase" && named.Value.Value is bool b)
                ignoreCase = b;
        }

        if (string.IsNullOrEmpty(pattern))
        {
            Report("TVAL005", Where(rule), "Pattern", member, "the pattern is empty");
            return null;
        }

        if (timeout <= 0)
        {
            Report("TVAL005", Where(rule), "Pattern", member, "TimeoutMilliseconds must be positive");
            return null;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            Report("TVAL005", Where(rule), "Pattern", member, "the pattern does not parse: " + e.Message);
            return null;
        }

        var name = "TarolPattern_" + member + "_" + _fields.Count.ToString(CultureInfo.InvariantCulture);
        var options = "global::System.Text.RegularExpressions.RegexOptions.CultureInvariant" +
                      (ignoreCase ? " | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase" : string.Empty);
        // Not [GeneratedRegex]: a source generator cannot feed another one (its output is not seen by the regex generator).
        // No RegexOptions.Compiled: it is ignored under Native AOT (finding 16). The timeout bounds a ReDoS input.
        _fields.Add("    private static readonly global::System.Text.RegularExpressions.Regex " + name +
                    " = new(" + SyntaxNames.Literal(pattern!) + ", " + options + ", global::System.TimeSpan.FromMilliseconds(" +
                    timeout.ToString(CultureInfo.InvariantCulture) + "));\n");
        return name;
    }

    // ---------------------------------------------------------------- helpers

    private void RejectRules(ImmutableArray<AttributeData> rules, string member, string typeText, string reason)
    {
        foreach (var rule in rules)
            Report("TVAL004", Where(rule), Symbols.RuleName(rule)!, member, typeText, reason);
    }

    private static string Applicability(string rule) => rule switch
    {
        "Range" => "[Range] applies to numbers",
        "Length" or "Pattern" or "Email" => $"[{rule}] applies to strings",
        "NotEmpty" => "[NotEmpty] applies to strings, Guid and ids",
        "Count" => "[Count] applies to arrays and IReadOnlyList<T>",
        _ => "not supported"
    };

    private static bool Collection(ITypeSymbol type, out ITypeSymbol? element, out string? countProperty)
    {
        element = null;
        countProperty = null;
        if (type.SpecialType == SpecialType.System_String)
            return false;

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            countProperty = "Length";
            return true;
        }

        if (type is not INamedTypeSymbol named)
            return false;

        foreach (var candidate in new[] { named }.Concat(named.AllInterfaces))
        {
            if (candidate is { Arity: 1 } && candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IReadOnlyList_T)
            {
                element = candidate.TypeArguments[0];
                countProperty = "Count";
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownLeaf(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum)
            return true;

        return type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly"
            or "System.TimeSpan" or "System.Uri" or "System.Half" or "System.Int128" or "System.UInt128";
    }

    private static bool IsComparable(ITypeSymbol type)
        => type.AllInterfaces.Any(i => i is { Arity: 1, MetadataName: "IComparable`1" }
                                       && i.ContainingNamespace?.ToDisplayString() == "System"
                                       && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type));

    private static bool IsFormattable(ITypeSymbol type)
        => type.AllInterfaces.Any(static i => i.MetadataName == "IFormattable" && i.ContainingNamespace?.ToDisplayString() == "System");

    internal static (string Read, string Write, string Cast)? JsonAccess(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => ("GetString", "WriteStringValue", ""),
        SpecialType.System_Boolean => ("GetBoolean", "WriteBooleanValue", ""),
        SpecialType.System_Byte => ("GetByte", "WriteNumberValue", "(uint)"),
        SpecialType.System_SByte => ("GetSByte", "WriteNumberValue", "(int)"),
        SpecialType.System_Int16 => ("GetInt16", "WriteNumberValue", "(int)"),
        SpecialType.System_UInt16 => ("GetUInt16", "WriteNumberValue", "(uint)"),
        SpecialType.System_Int32 => ("GetInt32", "WriteNumberValue", ""),
        SpecialType.System_UInt32 => ("GetUInt32", "WriteNumberValue", ""),
        SpecialType.System_Int64 => ("GetInt64", "WriteNumberValue", ""),
        SpecialType.System_UInt64 => ("GetUInt64", "WriteNumberValue", ""),
        SpecialType.System_Single => ("GetSingle", "WriteNumberValue", ""),
        SpecialType.System_Double => ("GetDouble", "WriteNumberValue", ""),
        SpecialType.System_Decimal => ("GetDecimal", "WriteNumberValue", ""),
        SpecialType.System_DateTime => ("GetDateTime", "WriteStringValue", ""),
        _ => type.ToDisplayString() switch
        {
            "System.Guid" => ("GetGuid", "WriteStringValue", ""),
            "System.DateTimeOffset" => ("GetDateTimeOffset", "WriteStringValue", ""),
            _ => null
        }
    };

    /// <summary>A module initializer must be reachable from the module: the type and its containers are public or internal.</summary>
    private static bool IsModuleVisible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        }

        return true;
    }

    private bool HasMember(string name) => _type.GetMembers(name).Any(static m => !m.IsImplicitlyDeclared);

    private bool HasUserMember(string name) => _type.GetMembers(name).Any(static m => !m.IsImplicitlyDeclared);

    private bool HasConversionTo(ITypeSymbol target)
        => _type.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Conversion
                                                               && SymbolEqualityComparer.Default.Equals(m.ReturnType, target));

    private bool HasJsonConverterAttribute()
        => _type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterAttribute");

    private string Temp() => "v" + (_temp++).ToString(CultureInfo.InvariantCulture);

    private static Location? Where(AttributeData rule) => rule.ApplicationSyntaxReference?.GetSyntax().GetLocation();

    private void Report(string id, Location? location, params string[] arguments)
        => _diagnostics.Add(new DiagnosticInfo(id, location is null ? null : LocationInfo.From(location),
            new EquatableArray<string>(arguments.ToImmutableArray())));

    private static string EscapeXml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Wraps members into the namespace, the containing types and the partial declaration.</summary>
    private string Wrap(string members, string? attributes, List<string>? interfaces = null)
    {
        var sb = new StringBuilder(members.Length + 512);
        sb.Append("// <auto-generated/>\n#nullable enable\n\n");

        var ns = _type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        if (ns is not null)
            sb.Append("namespace ").Append(ns).Append("\n{\n");

        var containers = new List<INamedTypeSymbol>();
        for (var c = _type.ContainingType; c is not null; c = c.ContainingType)
            containers.Insert(0, c);

        foreach (var container in containers)
            sb.Append(Declaration(container)).Append("\n{\n");

        if (attributes is not null)
            sb.Append(attributes).Append('\n');

        sb.Append(Declaration(_type));
        if (interfaces is { Count: > 0 })
            sb.Append(" : ").Append(string.Join(", ", interfaces));
        sb.Append("\n{\n").Append(members).Append("}\n");

        foreach (var _ in containers)
            sb.Append("}\n");

        if (ns is not null)
            sb.Append("}\n");

        return sb.ToString();
    }

    private static string Declaration(INamedTypeSymbol type)
    {
        var modifiers = new StringBuilder();
        if (type.IsStatic)
            modifiers.Append("static ");
        if (type.IsValueType && type.IsReadOnly)
            modifiers.Append("readonly ");
        if (type.IsRefLikeType)
            modifiers.Append("ref ");
        modifiers.Append("partial ");

        var keyword = type switch
        {
            { IsRecord: true, IsValueType: true } => "record struct",
            { IsRecord: true } => "record",
            { TypeKind: TypeKind.Struct } => "struct",
            { TypeKind: TypeKind.Interface } => "interface",
            _ => "class"
        };

        return modifiers + keyword + " " + type.Name;
    }
}
