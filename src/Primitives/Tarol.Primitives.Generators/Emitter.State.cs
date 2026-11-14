using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tarol.Primitives.Generators;

/// <summary>The stored-state part of the emitter: the schema hash and the generated <c>WriteState</c>/<c>ReadState</c>.</summary>
internal sealed partial class Emitter
{
    private const string Json = "global::System.Text.Json.";
    private const string StateJson = "global::Tarol.Primitives.StateJson";
    private const string Token = "global::System.Text.Json.JsonTokenType.";

    private readonly Dictionary<string, int> _names = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, (int Index, StateField Field)> _enums = new(System.StringComparer.Ordinal);

    /// <summary>The canonical description of the stored JSON shape: names, member shapes and nesting, in a fixed order.</summary>
    public string StateShape() => "tarol-state/1:" + Shape(_root, []);

    /// <summary>The schema: FNV-1a (32 bit, UTF-8) of <see cref="StateShape"/>, positive and never 0 (0 means no stored state).</summary>
    public int StateSchema()
    {
        if (_root.LoadByReplay)
            return 0;
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(StateShape()))
            {
                hash ^= b;
                hash *= 16777619u;
            }

            var schema = (int)(hash & 0x7FFFFFFF);
            return schema == 0 ? 1 : schema;
        }
    }

    private string Shape(TypeModel type, HashSet<string> path)
    {
        var sb = new StringBuilder("{id:id");
        if (type.FullName == RootName)
            sb.Append(",version:long,schema:int");
        foreach (var field in type.StateFields.OrderBy(static f => f.Name, System.StringComparer.Ordinal))
            sb.Append(',').Append(field.Name).Append(':').Append(field.Shape);
        path.Add(type.FullName);
        foreach (var (member, child) in StateChildren(type))
        {
            sb.Append(',').Append(member.StateName).Append(':');
            sb.Append(path.Contains(child.FullName) ? "#" + child.FullName : Shape(child, path));
            sb.Append(member.IsRef ? "?" : "[]");
        }

        path.Remove(type.FullName);
        return sb.Append('}').ToString();
    }

    /// <summary>The child collections of a type whose children are entities of this aggregate, by JSON name.</summary>
    private IEnumerable<(ContainerMember Member, TypeModel Child)> StateChildren(TypeModel type)
        => type.Members
            .Where(m => _entities.TryGetValue(m.Child, out var c) && c.CanEmit)
            .OrderBy(static m => m.StateName, System.StringComparer.Ordinal)
            .Select(m => (m, _entities[m.Child]));

    private IEnumerable<TypeModel> StateEntities()
        => _model.Entities.Where(e => e.CanEmit && _owners.ContainsKey(e.FullName));

    private void EmitState()
    {
        var root = RootName;
        var schema = StateSchema();
        Line("/// <summary>The shape of the stored state (a hash of the captured names and types); 0 when the aggregate loads by replay.</summary>");
        Line($"public static int StateSchema => {schema};");
        Line();

        if (_root.LoadByReplay)
        {
            Line("/// <summary>Throws: this aggregate is [LoadByReplay] and has no stored state.</summary>");
            Line("/// <param name=\"writer\">Not used.</param>");
            Line("/// <param name=\"aggregate\">Not used.</param>");
            Line("/// <param name=\"options\">Not used.</param>");
            Line($"public static void WriteState({Json}Utf8JsonWriter writer, {root} aggregate, {Json}JsonSerializerOptions options) => {P}ThrowHelper.NoStoredState({Literal(_root.Name)});");
            Line();
            Line("/// <summary>Skips the object and returns None: this aggregate is [LoadByReplay] and loads by replaying its events.</summary>");
            Line("/// <param name=\"id\">Not used.</param>");
            Line("/// <param name=\"reader\">The reader; the object is skipped.</param>");
            Line("/// <param name=\"options\">Not used.</param>");
            Line("/// <returns>None.</returns>");
            Line($"public static global::Tarol.Options.Option<{root}> ReadState({Id}<{root}> id, ref {Json}Utf8JsonReader reader, {Json}JsonSerializerOptions options)");
            Open();
            Line($"_ = {StateJson}.Begin(ref reader, 0, {Literal(_root.Name)});");
            Line("return default;");
            Close();
            Line();
            return;
        }

        foreach (var type in new[] { _root }.Concat(StateEntities()))
        {
            Name("id");
            foreach (var field in type.StateFields)
            {
                Name(field.Name);
                if (field.Kind == StateKind.Enum && !_enums.ContainsKey(field.Value))
                    _enums[field.Value] = (_enums.Count, field);
            }

            foreach (var (member, _) in StateChildren(type))
                Name(member.StateName);
        }

        Name("version");
        Name("schema");

        // ── write ──
        Line("/// <summary>Writes the current state of the aggregate and all its entities as one JSON object (no events).</summary>");
        Line("/// <param name=\"writer\">The writer.</param>");
        Line("/// <param name=\"aggregate\">The aggregate.</param>");
        Line("/// <param name=\"options\">The options for member values (value objects need ValueObjectJson.Resolver first).</param>");
        Line($"public static void WriteState({Json}Utf8JsonWriter writer, {root} aggregate, {Json}JsonSerializerOptions options)");
        Open();
        Line("global::System.ArgumentNullException.ThrowIfNull(writer);");
        Line("global::System.ArgumentNullException.ThrowIfNull(aggregate);");
        Line("global::System.ArgumentNullException.ThrowIfNull(options);");
        Line("if (aggregate.IsBroken)");
        Line($"    {P}ThrowHelper.Broken({Literal(_root.Name)});");
        Line("writer.WriteStartObject();");
        Line($"writer.WriteString({NameRef("id")}, aggregate.Id.Value);");
        Line($"writer.WriteNumber({NameRef("version")}, aggregate.Version);");
        Line($"writer.WriteNumber({NameRef("schema")}, {schema});");
        EmitWriteMembers(_root, "aggregate");
        Line("writer.WriteEndObject();");
        Close();
        Line();

        foreach (var entity in StateEntities())
        {
            var i = _index[entity.FullName];
            Line($"private static void __WriteState{i}({Json}Utf8JsonWriter writer, {entity.FullName} entity, {Json}JsonSerializerOptions options)");
            Open();
            Line("writer.WriteStartObject();");
            Line($"writer.WriteString({NameRef("id")}, entity.Id.Value);");
            EmitWriteMembers(entity, "entity");
            Line("writer.WriteEndObject();");
            Close();
            Line();
        }

        // ── read ──
        Line("/// <summary>");
        Line("/// Restores the aggregate from its stored state without events (no On handler, invariant or rule runs); None when the");
        Line("/// state has another schema (the object is skipped), so the caller replays instead.");
        Line("/// </summary>");
        Line("/// <param name=\"id\">The aggregate's id; the state's id must match.</param>");
        Line("/// <param name=\"reader\">A reader on, or before, the state object.</param>");
        Line("/// <param name=\"options\">The options the state was written with.</param>");
        Line("/// <returns>The aggregate, with no uncommitted changes, or None.</returns>");
        Line($"public static global::Tarol.Options.Option<{root}> ReadState({Id}<{root}> id, ref {Json}Utf8JsonReader reader, {Json}JsonSerializerOptions options)");
        Open();
        Line("global::System.ArgumentNullException.ThrowIfNull(options);");
        Line($"if (!{StateJson}.Begin(ref reader, {schema}, {Literal(_root.Name)}))");
        Line("    return default;");
        Line($"var aggregate = new {root}(id);");
        Line("var version = 0L;");
        Line("var found = 0;");
        Line($"while ({StateJson}.Next(ref reader, {Literal(_root.Name)}))");
        Open();
        Line("if (reader.ValueTextEquals(\"id\"u8))");
        Open();
        Line($"{StateJson}.Value(ref reader, {Literal(_root.Name)});");
        Line("var stored = reader.GetGuid();");
        Line("if (stored != id.Value)");
        Line($"    {P}ThrowHelper.ForeignState({Literal(_root.Name)}, id.Value, stored);");
        Line("found++;");
        Close();
        Line("else if (reader.ValueTextEquals(\"version\"u8))");
        Open();
        Line($"{StateJson}.Value(ref reader, {Literal(_root.Name)});");
        Line("version = reader.GetInt64();");
        Line("found++;");
        Close();
        Line("else if (reader.ValueTextEquals(\"schema\"u8))");
        Open();
        Line($"{StateJson}.Value(ref reader, {Literal(_root.Name)}); // checked by Begin");
        Line("found++;");
        Close();
        EmitReadMembers(_root, "aggregate");
        Line("else");
        Line($"    {StateJson}.Unknown(ref reader, {Literal(_root.Name)});");
        Close();
        var rootExpected = 3 + _root.StateFields.Count + StateChildren(_root).Count();
        Line($"if (found != {rootExpected})");
        Line($"    {StateJson}.Missing({Literal(_root.Name)}, {Literal(Expected(_root, true))});");
        Line($"__TarolRoot<{root}>.RestoreVersion(aggregate, version);");
        Line($"return new global::Tarol.Options.Option<{root}>(aggregate);");
        Close();
        Line();

        foreach (var entity in StateEntities())
        {
            var i = _index[entity.FullName];
            var x = entity.FullName;
            Line($"private {x} __ReadState{i}(ref {Json}Utf8JsonReader reader, {Json}JsonSerializerOptions options)");
            Open();
            Line($"var id = ({Id}<{x}>){StateJson}.FindId(reader, {Literal(entity.Name)});");
            Line($"if (__map{i}.ContainsKey(id))");
            Line($"    {P}ThrowHelper.DuplicateEntity({Literal(entity.Name)}, id.Value);");
            Line($"var entity = __New{i}(id, this);");
            Line($"__map{i}.Add(id, entity);");
            Line("var found = 0;");
            Line($"while ({StateJson}.Next(ref reader, {Literal(entity.Name)}))");
            Open();
            Line("if (reader.ValueTextEquals(\"id\"u8))");
            Open();
            Line($"{StateJson}.Value(ref reader, {Literal(entity.Name)}); // read by FindId");
            Line("found++;");
            Close();
            EmitReadMembers(entity, "entity");
            Line("else");
            Line($"    {StateJson}.Unknown(ref reader, {Literal(entity.Name)});");
            Close();
            Line($"if (found != {1 + entity.StateFields.Count + StateChildren(entity).Count()})");
            Line($"    {StateJson}.Missing({Literal(entity.Name)}, {Literal(Expected(entity, false))});");
            Line("return entity;");
            Close();
            Line();
        }

        foreach (var e in _enums.Values.OrderBy(static e => e.Index))
            EmitEnumHelpers(e.Index, e.Field);

        foreach (var name in _names.OrderBy(static n => n.Value))
            Line($"private static readonly {Json}JsonEncodedText __sn{name.Value} = {Json}JsonEncodedText.Encode({Literal(name.Key)});");
        Line();

        foreach (var field in _root.StateFields)
            Line($"{Accessor}Field, Name = {Literal(field.Field)})] private static extern ref {field.Type} {FieldAccessor(_root, field)}({root} owner);");
        foreach (var entity in StateEntities())
            foreach (var field in entity.StateFields)
                Line($"{Accessor}Field, Name = {Literal(field.Field)})] private static extern ref {field.Type} {FieldAccessor(entity, field)}({entity.FullName} owner);");
    }

    private void Name(string name)
    {
        if (!_names.ContainsKey(name))
            _names[name] = _names.Count;
    }

    private string NameRef(string name) => $"__sn{_names[name]}";

    private string FieldAccessor(TypeModel type, StateField field)
    {
        var n = type.StateFields.ToList().IndexOf(field);
        return type.FullName == RootName ? $"__SR{n}" : $"__S{_index[type.FullName]}_{n}";
    }

    private string Expected(TypeModel type, bool root)
    {
        var names = new List<string> { "id" };
        if (root)
            names.AddRange(["version", "schema"]);
        names.AddRange(type.StateFields.Select(static f => f.Name));
        names.AddRange(StateChildren(type).Select(static c => c.Member.StateName));
        return string.Join(", ", names.Select(static n => "\"" + n + "\""));
    }

    /// <summary>The container of an owner as seen from the static writers and the readers (no <c>this</c> for the root).</summary>
    private string StateContainer(TypeModel owner, ContainerMember member, string ownerExpr)
        => owner.FullName == RootName ? $"{ownerExpr}.{member.Name}" : $"__Member{_index[owner.FullName]}_{member.Name}({ownerExpr})";

    private void EmitWriteMembers(TypeModel type, string owner)
    {
        foreach (var field in type.StateFields.OrderBy(static f => f.Name, System.StringComparer.Ordinal))
        {
            Line($"writer.WritePropertyName({NameRef(field.Name)});");
            EmitWriteValue(type, field, $"{FieldAccessor(type, field)}({owner})");
        }

        foreach (var (member, child) in StateChildren(type))
        {
            var c = _index[child.FullName];
            var container = StateContainer(type, member, owner);
            if (member.IsRef)
            {
                Line($"writer.WritePropertyName({NameRef(member.StateName)});");
                Line($"if ({container}.Value is {{ }} child{c})");
                Line($"    __WriteState{c}(writer, child{c}, options);");
                Line("else");
                Line("    writer.WriteNullValue();");
            }
            else
            {
                Line($"writer.WriteStartArray({NameRef(member.StateName)});");
                Line($"foreach (var child{c} in {container})");
                Line($"    __WriteState{c}(writer, child{c}, options);");
                Line("writer.WriteEndArray();");
            }
        }
    }

    private void EmitWriteValue(TypeModel type, StateField field, string expr)
    {
        switch (field.Wrap)
        {
            case StateWrap.Nullable:
                Line($"if ({expr} is {{ }} value{FieldAccessor(type, field)})");
                Line($"    {WriteDirect(field, "value" + FieldAccessor(type, field))}");
                Line("else");
                Line("    writer.WriteNullValue();");
                return;
            case StateWrap.Option:
                Line($"if ({expr}.IsSome)");
                Line($"    {WriteDirect(field, expr + ".GetValueOrThrow()")}");
                Line("else");
                Line("    writer.WriteNullValue();");
                return;
        }

        switch (field.Kind)
        {
            case StateKind.String:
                Line($"if ({expr} is {{ }} value{FieldAccessor(type, field)})");
                Line($"    writer.WriteStringValue(value{FieldAccessor(type, field)});");
                Line("else");
                Line("    writer.WriteNullValue();");
                return;
            case StateKind.Json:
                Line($"{Json}JsonSerializer.Serialize(writer, {expr}, {StateJson}.Info<{field.Type}>(options, {Literal(type.Name + "." + field.Member)}));");
                return;
            default:
                Line(WriteDirect(field, expr));
                return;
        }
    }

    private string WriteDirect(StateField field, string value) => field.Kind switch
    {
        StateKind.String or StateKind.Guid or StateKind.DateTime or StateKind.DateTimeOffset => $"writer.WriteStringValue({value});",
        StateKind.Bool => $"writer.WriteBooleanValue({value});",
        StateKind.Number => $"writer.WriteNumberValue({value});",
        StateKind.Id => $"writer.WriteStringValue({value}.Value);",
        StateKind.Enum => $"__WriteEnum{_enums[field.Value].Index}(writer, {value});",
        _ => throw new System.InvalidOperationException("not a direct kind: " + field.Kind),
    };

    private void EmitReadMembers(TypeModel type, string owner)
    {
        var name = Literal(type.Name);
        foreach (var field in type.StateFields)
        {
            Line($"else if (reader.ValueTextEquals({Literal(field.Name)}u8))");
            Open();
            Line($"{StateJson}.Value(ref reader, {name});");
            Line($"{FieldAccessor(type, field)}({owner}) = {ReadValue(type, field)};");
            Line("found++;");
            Close();
        }

        foreach (var (member, child) in StateChildren(type))
        {
            var c = _index[child.FullName];
            var container = StateContainer(type, member, owner);
            var self = type.FullName == RootName ? "aggregate." : "";
            Line($"else if (reader.ValueTextEquals({Literal(member.StateName)}u8))");
            Open();
            if (member.IsRef)
            {
                Line($"{StateJson}.Value(ref reader, {name});");
                Line($"if (reader.TokenType != {Token}Null)");
                Open();
                Line($"var child = {self}__ReadState{c}(ref reader, options);");
                Line($"__TarolRef<{child.FullName}>.Set({container}, child);");
                Line($"__Parent{c}(child) = {owner};");
                Close();
            }
            else
            {
                Line($"{StateJson}.StartArray(ref reader, {name});");
                Line($"while ({StateJson}.NextItem(ref reader, {name}))");
                Open();
                Line($"var child = {self}__ReadState{c}(ref reader, options);");
                Line($"__TarolSet<{child.FullName}>.Add({container}, child);");
                Line($"__Parent{c}(child) = {owner};");
                Close();
            }

            Line("found++;");
            Close();
        }
    }

    private string ReadValue(TypeModel type, StateField field)
    {
        var direct = field.Kind switch
        {
            StateKind.String => "reader.GetString()!",
            StateKind.Id => $"({field.Value})reader.GetGuid()",
            StateKind.Enum => $"__ReadEnum{(_enums.TryGetValue(field.Value, out var e) ? e.Index : -1)}(ref reader)",
            StateKind.Json => $"{Json}JsonSerializer.Deserialize(ref reader, {StateJson}.Info<{field.Type}>(options, {Literal(type.Name + "." + field.Member)}))!",
            _ => $"reader.{field.ReadMethod}()",
        };
        return field.Wrap switch
        {
            StateWrap.Nullable => $"reader.TokenType == {Token}Null ? null : ({field.Value}?){direct}",
            StateWrap.Option => $"reader.TokenType == {Token}Null ? default : new global::Tarol.Options.Option<{field.Value}>({direct})",
            _ => direct,
        };
    }

    private void EmitEnumHelpers(int index, StateField field)
    {
        var e = field.Value;
        Line($"private static void __WriteEnum{index}({Json}Utf8JsonWriter writer, {e} value)");
        Open();
        Line("switch (value)");
        Open();
        foreach (var member in field.EnumMembers)
            Line($"case {e}.{member}: writer.WriteStringValue({Literal(member)}u8); return;");
        Line($"default: writer.WriteNumberValue(({field.EnumUnderlying})value); return;");
        Close();
        Close();
        Line();
        Line($"private static {e} __ReadEnum{index}(ref {Json}Utf8JsonReader reader)");
        Open();
        Line($"if (reader.TokenType == {Token}Number)");
        Line($"    return ({e})reader.{UnderlyingRead(field.EnumUnderlying)}();");
        foreach (var member in field.EnumMembers)
        {
            Line($"if (reader.ValueTextEquals({Literal(member)}u8))");
            Line($"    return {e}.{member};");
        }

        Line($"{StateJson}.UnknownName(ref reader, {Literal(Short(e))});");
        Line("return default;");
        Close();
        Line();
    }

    private static string UnderlyingRead(string underlying) => underlying switch
    {
        "byte" => "GetByte",
        "sbyte" => "GetSByte",
        "short" => "GetInt16",
        "ushort" => "GetUInt16",
        "uint" => "GetUInt32",
        "long" => "GetInt64",
        "ulong" => "GetUInt64",
        _ => "GetInt32",
    };
}
