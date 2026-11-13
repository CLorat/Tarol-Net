using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace Tarol.Results;

/// <summary>
/// Why an operation did not succeed. A violation is exactly one of two kinds:
/// <list type="bullet">
///   <item><description><see cref="Invalid"/>: the input is invalid; carries one or more <see cref="FieldError"/>s (HTTP 400).</description></item>
///   <item><description><see cref="Error"/>: one light, non-fatal error such as not found or conflict, with an <see cref="ErrorType"/> and a code.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is <c>closed</c>: no other assembly can add a kind, so a <c>switch</c> over
/// <c>Invalid</c> and <c>Error</c> is exhaustive without a default arm. New situations are new codes, not new kinds.
/// </para>
/// <para>
/// JSON: <c>{"kind":"invalid","errors":[{"field":…,"code":…}]}</c> or
/// <c>{"kind":"error","type":"NotFound","code":…,"detail":…}</c>. The names are fixed by this package and do not
/// follow the serializer's naming policy, so persisted violations stay readable.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Invalid), "invalid")]
[JsonDerivedType(typeof(Error), "error")]
public closed record Violation
{
    /// <summary>Returns the HTTP status code for this violation: 400 for <see cref="Invalid"/>, the type's code for <see cref="Error"/>.</summary>
    /// <returns>The status code.</returns>
    public abstract int ToHttpStatusCode();
}

/// <summary>One validation problem of one field.</summary>
public sealed record FieldError
{
    /// <summary>Creates a field error.</summary>
    /// <param name="field">The field or property path (for example <c>"Address.ZipCode"</c>); empty for an object-level error.</param>
    /// <param name="code">A stable code such as <c>"Name.Required"</c>.</param>
    /// <param name="detail">An optional human-readable explanation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty.</exception>
    public FieldError(string field, string code, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrEmpty(code);
        Field = field;
        Code = code;
        Detail = detail;
    }

    /// <summary>The field or property path; empty for an object-level error.</summary>
    [JsonPropertyName("field")]
    public string Field { get; }

    /// <summary>A stable code such as <c>"Name.Required"</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; }

    /// <summary>An optional human-readable explanation.</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; }
}

/// <summary>The input is invalid: one or more <see cref="FieldError"/>s. HTTP 400.</summary>
public sealed record Invalid : Violation
{
    /// <summary>Creates a validation violation from a non-empty list of field errors (the list is copied).</summary>
    /// <param name="errors">The field errors.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> or one of its items is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="errors"/> is empty.</exception>
    [JsonConstructor]
    public Invalid(IReadOnlyList<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var copy = new FieldError[errors.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = errors[i] ?? throw new ArgumentNullException(nameof(errors), "A field error is null.");
        }

        Errors = copy.Length > 0 ? copy : throw new ArgumentException("An Invalid violation needs at least one field error.", nameof(errors));
    }

    /// <summary>Creates a validation violation from one or more field errors.</summary>
    /// <param name="errors">The field errors.</param>
    /// <exception cref="ArgumentException"><paramref name="errors"/> is empty.</exception>
    public Invalid(params ReadOnlySpan<FieldError> errors)
        : this((IReadOnlyList<FieldError>)errors.ToArray())
    {
    }

    /// <summary>The field errors, never empty.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<FieldError> Errors { get; }

    /// <summary>Creates a validation violation with a single field error.</summary>
    /// <param name="field">The field or property path.</param>
    /// <param name="code">A stable code such as <c>"Name.Required"</c>.</param>
    /// <param name="detail">An optional human-readable explanation.</param>
    /// <returns>The violation.</returns>
    public static Invalid Field(string field, string code, string? detail = null) => new(new FieldError(field, code, detail));

    /// <summary>Returns a violation with the field errors of this one followed by those of <paramref name="other"/>.</summary>
    /// <param name="other">The other validation violation.</param>
    /// <returns>The combined violation.</returns>
    public Invalid Combine(Invalid other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Invalid([.. Errors, .. other.Errors]);
    }

    /// <inheritdoc />
    public override int ToHttpStatusCode() => 400;

    /// <summary>Compares the field errors element by element.</summary>
    /// <param name="other">The other violation.</param>
    /// <returns><see langword="true"/> when both contain equal field errors in the same order.</returns>
    public bool Equals(Invalid? other) => other is not null && Errors.SequenceEqual(other.Errors);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var error in Errors)
        {
            hash.Add(error);
        }

        return hash.ToHashCode();
    }

    /// <summary>Lists the field errors, for example <c>Invalid { Name: Name.Required; Email: Email.Invalid }</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var text = new StringBuilder("Invalid { ");
        for (var i = 0; i < Errors.Count; i++)
        {
            if (i > 0)
            {
                text.Append("; ");
            }

            text.Append(Errors[i].Field).Append(": ").Append(Errors[i].Code);
        }

        return text.Append(" }").ToString();
    }
}

/// <summary>One light, non-fatal error: what went wrong (<see cref="Type"/>), a stable <see cref="Code"/> and an optional <see cref="Detail"/>.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Owner decision (docs/migration/results.md Q-2): the light-error case is named Error; Tarol targets C# only and the VB keyword clash does not apply.")]
public sealed record Error : Violation
{
    /// <summary>Creates an error.</summary>
    /// <param name="type">The kind of error; decides the HTTP status code.</param>
    /// <param name="code">A stable code such as <c>"User.NotFound"</c>.</param>
    /// <param name="detail">An optional human-readable explanation. Do not put secrets or stack traces here: it may reach clients.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is <see langword="null"/> or empty.</exception>
    [JsonConstructor]
    public Error(ErrorType type, string code, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Type = type;
        Code = code;
        Detail = detail;
    }

    /// <summary>The kind of error.</summary>
    [JsonPropertyName("type")]
    public ErrorType Type { get; }

    /// <summary>A stable code such as <c>"User.NotFound"</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; }

    /// <summary>An optional human-readable explanation.</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; }

    /// <summary>
    /// What <c>default(Result)</c> and <c>default(Result&lt;T&gt;)</c> read as: type <see cref="ErrorType.Internal"/>,
    /// code <c>"Result.Uninitialized"</c>. Seeing it means a result was never assigned.
    /// </summary>
    public static Error Uninitialized { get; } = new(ErrorType.Internal, "Result.Uninitialized",
        "default(Result) was used: the result was never assigned a success or a violation.");

    /// <summary>Creates an <see cref="ErrorType.NotFound"/> error.</summary>
    /// <param name="code">The code, for example <c>"User.NotFound"</c>.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string code, string? detail = null) => new(ErrorType.NotFound, code, detail);

    /// <summary>
    /// Creates an <see cref="ErrorType.NotFound"/> error for an entity: code <c>"{TEntity}.NotFound"</c>, detail
    /// <c>"{TEntity} '{id}' was not found."</c>. The id is formatted without boxing.
    /// </summary>
    /// <typeparam name="TEntity">The entity type; its simple name is used.</typeparam>
    /// <typeparam name="TId">The id type, for example <c>Id&lt;User&gt;</c> or <see cref="Guid"/>.</typeparam>
    /// <param name="id">The id that was not found.</param>
    /// <returns>The error.</returns>
    public static Error NotFound<TEntity, TId>(TId id)
    {
        var name = typeof(TEntity).Name;
        return new(ErrorType.NotFound, string.Concat(name, ".NotFound"), $"{name} '{id}' was not found.");
    }

    /// <summary>Creates an <see cref="ErrorType.Conflict"/> error.</summary>
    /// <param name="code">The code, for example <c>"User.EmailTaken"</c>.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Conflict(string code, string? detail = null) => new(ErrorType.Conflict, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Forbidden"/> error.</summary>
    /// <param name="code">The code; defaults to <c>"Auth.Forbidden"</c>.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(string code = "Auth.Forbidden", string? detail = null) => new(ErrorType.Forbidden, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Unauthorized"/> error.</summary>
    /// <param name="code">The code; defaults to <c>"Auth.Unauthorized"</c>.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Unauthorized(string code = "Auth.Unauthorized", string? detail = null) => new(ErrorType.Unauthorized, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Gone"/> error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Gone(string code, string? detail = null) => new(ErrorType.Gone, code, detail);

    /// <summary>Creates an <see cref="ErrorType.UnprocessableEntity"/> error (a broken business rule).</summary>
    /// <param name="code">The code, for example <c>"Order.AlreadyShipped"</c>.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error UnprocessableEntity(string code, string? detail = null) => new(ErrorType.UnprocessableEntity, code, detail);

    /// <summary>Creates an <see cref="ErrorType.TooManyRequests"/> error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error TooManyRequests(string code, string? detail = null) => new(ErrorType.TooManyRequests, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Unavailable"/> error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Unavailable(string code, string? detail = null) => new(ErrorType.Unavailable, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Timeout"/> error.</summary>
    /// <param name="code">The code.</param>
    /// <param name="detail">An optional explanation.</param>
    /// <returns>The error.</returns>
    public static Error Timeout(string code, string? detail = null) => new(ErrorType.Timeout, code, detail);

    /// <summary>Creates an <see cref="ErrorType.Internal"/> error.</summary>
    /// <param name="code">The code, for example <c>"Database.Unreachable"</c>.</param>
    /// <param name="detail">An optional explanation. It may reach clients: no secrets, no stack traces.</param>
    /// <returns>The error.</returns>
    public static Error Internal(string code, string? detail = null) => new(ErrorType.Internal, code, detail);

    /// <inheritdoc />
    public override int ToHttpStatusCode() => Type.ToHttpStatusCode();

    /// <summary>Returns <c>Error { Type, Code }</c>, for example <c>Error { NotFound, User.NotFound }</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"Error {{ {Type}, {Code} }}";
}
