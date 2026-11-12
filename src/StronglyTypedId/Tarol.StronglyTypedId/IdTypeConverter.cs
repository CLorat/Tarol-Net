using System.ComponentModel;
using System.Globalization;

namespace Tarol.StronglyTypedId;

/// <summary>
///     <see cref="TypeConverter" /> for <see cref="Id{T}" />, converting to/from
///     <see cref="string" /> and <see cref="Guid" />.
///     <para>
///         This is what makes classic ASP.NET Core MVC model binding work for route and
///         query parameters (<c>/users/{id}</c>), and also enables
///         <c>Microsoft.Extensions.Configuration</c> binding and designer scenarios.
///         (Minimal APIs bind via <see cref="ISpanParsable{TSelf}" /> and don't need this.)
///     </para>
///     <para>
///         NOT wired up via an attribute on the struct: an open-generic
///         <c>[TypeConverter(typeof(IdTypeConverter&lt;&gt;))]</c> would require runtime
///         <c>MakeGenericType</c> inside TypeDescriptor — not statically analyzable under
///         NativeAOT. Instead the source generator registers closed instances at assembly
///         load via <see cref="IdConverterRegistry.Register{T}" />; assemblies compiled
///         without the generator can call that method manually once at startup.
///     </para>
/// </summary>
/// <typeparam name="T">The phantom entity type.</typeparam>
public sealed class IdTypeConverter<T> : TypeConverter
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string)
               || sourceType == typeof(Guid)
               || base.CanConvertFrom(context, sourceType);
    }

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value switch
        {
            string s => Id<T>.Parse(s, culture),
            Guid g => new Id<T>(g),
            _ => base.ConvertFrom(context, culture, value)
        };
    }

    /// <inheritdoc />
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
    {
        return destinationType == typeof(string)
               || destinationType == typeof(Guid)
               || base.CanConvertTo(context, destinationType);
    }

    /// <inheritdoc />
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value,
        Type destinationType)
    {
        if (value is Id<T> id)
        {
            if (destinationType == typeof(string))
                return id.ToString();
            if (destinationType == typeof(Guid))
                return id.Value;
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
