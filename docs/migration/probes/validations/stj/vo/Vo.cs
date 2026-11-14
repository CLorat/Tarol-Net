using Tarol.Validations;
namespace Lib;
public readonly partial record struct Quantity([Range(1, 100)] int Value);
public sealed record LibEvent(Quantity Total, Tarol.Options.Option<Quantity> Bonus);
[System.Text.Json.Serialization.JsonSerializable(typeof(LibEvent))]
public partial class LibJson : System.Text.Json.Serialization.JsonSerializerContext;
