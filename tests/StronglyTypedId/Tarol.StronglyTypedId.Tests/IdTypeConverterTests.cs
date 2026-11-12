using System.ComponentModel;
using System.Globalization;

namespace Tarol.StronglyTypedId.Tests;

public class IdTypeConverterTests
{
    private static readonly Guid KnownGuid = Guid.Parse("018f2b3c-1234-7abc-9def-0123456789ab");

    public IdTypeConverterTests()
    {
        IdConverterRegistry.Register<ConverterMarker>();
        // idempotent; safe per-test
    }

    [Fact]
    public void Registration_Makes_TypeDescriptor_Resolve_Our_Converter()
    {
        // There is no [TypeConverter] attribute on the struct anymore (AOT), so this
        // resolving correctly is proof the runtime registration path works.
        TypeDescriptor.GetConverter(typeof(Id<ConverterMarker>))
            .ShouldBeOfType<IdTypeConverter<ConverterMarker>>();
    }

    [Fact]
    public void Double_Registration_Is_A_NoOp_Not_A_Provider_Stack()
    {
        IdConverterRegistry.Register<ConverterMarker>();
        IdConverterRegistry.Register<ConverterMarker>();

        TypeDescriptor.GetConverter(typeof(Id<ConverterMarker>))
            .ShouldBeOfType<IdTypeConverter<ConverterMarker>>();
    }

    [Fact]
    public void Unregistered_Marker_Gets_No_Special_Converter()
    {
        // Reflection-constructed so the syntax-driven generator can never register it,
        // even when the generator runs on this assembly. See the matching JSON test.
        var idType = typeof(Id<>).MakeGenericType(typeof(FallbackMarker));

        var converter = TypeDescriptor.GetConverter(idType);

        (converter.GetType().IsGenericType &&
         converter.GetType().GetGenericTypeDefinition() == typeof(IdTypeConverter<>))
            .ShouldBeFalse();
    }

    [Fact]
    public void ConvertFrom_String_Parses()
    {
        var converter = new IdTypeConverter<ConverterMarker>();

        var result = converter.ConvertFromString(KnownGuid.ToString());

        result.ShouldBeOfType<Id<ConverterMarker>>().Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void ConvertFrom_Guid_Wraps()
    {
        var converter = new IdTypeConverter<ConverterMarker>();

        var result = converter.ConvertFrom(KnownGuid);

        result.ShouldBeOfType<Id<ConverterMarker>>().Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void ConvertTo_String_And_Guid_Unwrap()
    {
        var converter = new IdTypeConverter<ConverterMarker>();
        var id = Id<ConverterMarker>.From(KnownGuid);

        converter.ConvertToString(id).ShouldBe(KnownGuid.ToString());
        converter.ConvertTo(id, typeof(Guid)).ShouldBe(KnownGuid);
    }

    [Fact]
    public void ConvertFrom_Invalid_String_Throws_FormatException()
    {
        Should.Throw<FormatException>(() => new IdTypeConverter<ConverterMarker>().ConvertFromString("garbage"));
    }

    [Fact]
    public void Converter_Ignores_Arbitrary_Descriptor_Context()
    {
        // MVC hands the converter whatever context it has; the converter must not
        // depend on it. The stub throws on any member access.
        var context = new ThrowingContext();
        var converter = new IdTypeConverter<ConverterMarker>();

        converter.CanConvertFrom(context, typeof(string)).ShouldBeTrue();
        converter.CanConvertFrom(context, typeof(Guid)).ShouldBeTrue();
        converter.CanConvertTo(context, typeof(string)).ShouldBeTrue();
        converter.CanConvertTo(context, typeof(Guid)).ShouldBeTrue();

        converter.ConvertFrom(context, CultureInfo.InvariantCulture, KnownGuid.ToString())
            .ShouldBeOfType<Id<ConverterMarker>>().Value.ShouldBe(KnownGuid);
    }

    private sealed class ThrowingContext : ITypeDescriptorContext
    {
        public IContainer Container => throw new InvalidOperationException();
        public object Instance => throw new InvalidOperationException();
        public PropertyDescriptor PropertyDescriptor => throw new InvalidOperationException();
        public object? GetService(Type serviceType) => throw new InvalidOperationException();
        public void OnComponentChanged() => throw new InvalidOperationException();
        public bool OnComponentChanging() => throw new InvalidOperationException();
    }
}
