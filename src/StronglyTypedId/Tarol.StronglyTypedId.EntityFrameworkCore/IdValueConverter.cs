using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tarol.StronglyTypedId.EntityFrameworkCore;

/// <summary>
///     Converts <see cref="Id{T}" /> to and from <see cref="Guid" /> for EF Core.
///     No value comparer is needed: <see cref="Id{T}" /> is an immutable value type with structural equality.
/// </summary>
/// <typeparam name="T">The phantom entity type.</typeparam>
public sealed class IdValueConverter<T>() : ValueConverter<Id<T>, Guid>(
    id => id.Value,
    guid => Id<T>.From(guid));
