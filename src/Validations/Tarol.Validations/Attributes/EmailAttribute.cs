namespace Tarol.Validations;

/// <summary>The text has the shape of an e-mail address (<see cref="Rules.Email(System.ReadOnlySpan{char})" />). Code <see cref="ValidationCodes.Email" />.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class EmailAttribute : Attribute;
