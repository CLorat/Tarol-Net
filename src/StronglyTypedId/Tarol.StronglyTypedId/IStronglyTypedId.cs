namespace Tarol.StronglyTypedId;

/// <summary>
///     Marker interface implemented by every <see cref="Id{T}" />.
///     <para>
///         Enables non-generic access to the underlying value and lets consumers write
///         infrastructure conventions that treat every strongly-typed ID the same way
///         (for example a persistence mapping that stores every ID as a uuid column).
///     </para>
/// </summary>
public interface IStronglyTypedId
{
    /// <summary>Gets the underlying raw <see cref="Guid" /> value.</summary>
    Guid Value { get; }
}
