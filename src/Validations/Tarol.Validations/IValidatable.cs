namespace Tarol.Validations;

/// <summary>
///     An input (command, query, request) whose members are validated before it reaches the domain.
/// </summary>
/// <remarks>
///     <para>
///         Declare it on a <c>partial record</c> and the bundled generator implements <see cref="Validate" /> from the
///         primary-constructor parameters: each value object's rules, <c>Id&lt;T&gt;.Empty</c> as <c>Required</c>,
///         <c>Option&lt;T&gt;</c> as optional, collections element by element, nested inputs, and rule attributes on
///         primitive parameters. A hand-written implementation is allowed; then the generator does nothing.
///     </para>
///     <para>Call it through <see cref="ValidatableExtensions.Validated{T}(T)" />.</para>
/// </remarks>
public interface IValidatable
{
    /// <summary>Adds an error to <paramref name="errors" /> for every rule this input breaks, at the input's path.</summary>
    /// <param name="errors">The collector; pass it on by <see langword="ref" />.</param>
    void Validate(ref ValidationErrors errors);
}
