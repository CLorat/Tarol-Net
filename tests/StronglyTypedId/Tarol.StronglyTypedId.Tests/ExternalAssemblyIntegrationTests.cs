using System.ComponentModel;
using System.Text.Json;
using Tarol.StronglyTypedId.Tests.ExternalAssembly;

namespace Tarol.StronglyTypedId.Tests;

/// <summary>
///     End-to-end proof of the per-assembly design: the marker type is an INTERFACE
///     declared in a DIFFERENT assembly, this test assembly never calls Register for it,
///     and everything must work purely because the generator ran on the external assembly
///     and its [ModuleInitializer] fired at load.
/// </summary>
public class ExternalAssemblyIntegrationTests
{
    private static readonly Guid KnownGuid = Guid.Parse("018f2b3c-1234-7abc-9def-0123456789ab");

    [Fact]
    public void External_Assembly_Module_Initializer_Registered_The_TypeConverter()
    {
        _ = new ExternalAssemblyTestClassWithId();

        // Strongest possible assertion: since Id<T> carries NO [TypeConverter]
        // attribute, this can only resolve if the external assembly's generated
        // registration actually executed.
        TypeDescriptor.GetConverter(typeof(Id<IExternalAssemblyTestClassWithId>))
            .ShouldBeOfType<IdTypeConverter<IExternalAssemblyTestClassWithId>>();
    }

    [Fact]
    public void External_Dto_With_Interface_Marker_Round_Trips_Through_Json()
    {
        var dto = new ExternalAssemblyTestClassWithId
        {
            Id = Id<IExternalAssemblyTestClassWithId>.From(KnownGuid)
        };

        var json = JsonSerializer.Serialize(dto);
        json.ShouldContain(KnownGuid.ToString());

        var roundTripped = JsonSerializer.Deserialize<ExternalAssemblyTestClassWithId>(json);
        roundTripped.ShouldNotBeNull();
        roundTripped.Id.Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void External_Id_Binds_From_Route_Style_String_Via_TypeConverter()
    {
        _ = new ExternalAssemblyTestClassWithId();

        var converter = TypeDescriptor.GetConverter(typeof(Id<IExternalAssemblyTestClassWithId>));
        var bound = converter.ConvertFromString(KnownGuid.ToString());

        bound.ShouldBeOfType<Id<IExternalAssemblyTestClassWithId>>().Value.ShouldBe(KnownGuid);
    }
}
