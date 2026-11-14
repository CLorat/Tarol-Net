namespace Tarol.Validations;

/// <summary>
///     The value is not empty: a text with at least one non-white-space character, a <see cref="Guid" /> other than
///     <see cref="Guid.Empty" />, an <c>Id&lt;T&gt;</c> other than <c>Id&lt;T&gt;.Empty</c>. Code <see cref="ValidationCodes.Required" />.
/// </summary>
/// <remarks>
///     <see langword="null" /> is already <see cref="ValidationCodes.Required" /> for every non-optional member; use this
///     attribute when empty or blank text must be rejected too. For collections use <c>[Count(Min = 1)]</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class NotEmptyAttribute : Attribute;
