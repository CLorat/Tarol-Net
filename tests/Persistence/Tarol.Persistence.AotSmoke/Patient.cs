using System.Text.Json.Serialization;
using Tarol.Primitives;
using Tarol.StronglyTypedId;

namespace Tarol.Persistence.AotSmoke;

/// <summary>Step 16A: an aggregate with personal data in its events and state, sealed natively.</summary>
public partial class Patient : AggregateRoot<Patient>
{
    [PersonalData] public string? Name { get; private set; }
    public string Ward { get; private set; } = "";

    public static Patient Admit(string name, string ward)
    {
        var patient = new Patient(Id<Patient>.New());
        patient.Raise(new Admitted(name, ward));
        return patient;
    }

    public Tarol.Results.Success Rename(string name) => Raise(new Renamed(name));

    public sealed record Admitted([property: PersonalData] string? Name, string Ward) : Event;
    public sealed record Renamed([property: PersonalData] string? Name) : Event;

    private void On(Admitted e) { Name = e.Name; Ward = e.Ward; }
    private void On(Renamed e) => Name = e.Name;
}

[JsonSerializable(typeof(Patient.Admitted))]
[JsonSerializable(typeof(Patient.Renamed))]
internal sealed partial class PatientJson : JsonSerializerContext;
