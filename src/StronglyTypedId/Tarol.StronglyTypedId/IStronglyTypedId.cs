namespace Tarol.StronglyTypedId;

/// <summary>
///     Marker interface implemented by every <see cref="Id{T}" />.
///     <para>
///         Enables non-generic access to the underlying value and lets consumers write
///         infrastructure conventions (e.g., an EF Core model convention that applies a
///         value converter to every strongly-typed ID property in one place).
///     </para>
/// </summary>
public interface IStronglyTypedId
{
    /// <summary>Gets the underlying raw <see cref="Guid" /> value.</summary>
    Guid Value { get; }
}
