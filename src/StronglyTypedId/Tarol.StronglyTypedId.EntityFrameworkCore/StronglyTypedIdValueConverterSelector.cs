using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tarol.StronglyTypedId.EntityFrameworkCore;

/// <summary>
///     Offers <see cref="IdValueConverter{T}" /> for every <see cref="Id{T}" /> (and <c>Id&lt;T&gt;?</c>) CLR type.
///     EF Core asks the selector when it finds a property type it cannot map by itself, so every strongly-typed
///     id property in the model maps to a <see cref="Guid" /> column, whatever its marker type (class,
///     interface, struct), without listing types or scanning assemblies.
///     <para>Installed by <see cref="StronglyTypedIdDbContextOptionsBuilderExtensions.UseStronglyTypedIds" />.</para>
/// </summary>
/// <param name="dependencies">EF Core service dependencies.</param>
public sealed class StronglyTypedIdValueConverterSelector(ValueConverterSelectorDependencies dependencies)
    : ValueConverterSelector(dependencies)
{
    private readonly ConcurrentDictionary<Type, ValueConverterInfo> _converters = new();

    /// <inheritdoc />
    public override IEnumerable<ValueConverterInfo> Select(Type modelClrType, Type? providerClrType = null)
    {
        ArgumentNullException.ThrowIfNull(modelClrType);

        var idType = Nullable.GetUnderlyingType(modelClrType) ?? modelClrType;
        var provider = providerClrType is null ? null : Nullable.GetUnderlyingType(providerClrType) ?? providerClrType;
        if (IsStronglyTypedId(idType) && (provider is null || provider == typeof(Guid)))
            yield return _converters.GetOrAdd(idType, Create);

        foreach (var converter in base.Select(modelClrType, providerClrType))
            yield return converter;
    }

    private static bool IsStronglyTypedId(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Id<>);

    private static ValueConverterInfo Create(Type idType)
    {
        // MakeGenericType is why this package is not AOT-compatible (EF Core model building is not either).
        var converterType = typeof(IdValueConverter<>).MakeGenericType(idType.GetGenericArguments()[0]);
        return new ValueConverterInfo(
            idType,
            typeof(Guid),
            _ => (ValueConverter)Activator.CreateInstance(converterType)!);
    }
}
