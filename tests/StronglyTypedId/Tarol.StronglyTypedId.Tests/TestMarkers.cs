namespace Tarol.StronglyTypedId.Tests;

// Phantom markers. Deliberately empty — that's the whole point of a phantom type.
public sealed class UserMarker;

public sealed class OrderMarker;

public sealed class RegistryMarker; // used only by registry tests

public sealed class ConverterMarker; // used only by TypeConverter tests

// INVARIANT: the closed generic Id<FallbackMarker> must NEVER be spelled in source
// anywhere in this solution. Fallback tests construct it via typeof(Id<>).MakeGenericType
// so the syntax-driven generator cannot see it — that's what makes "unregistered"
// a guarantee instead of an accident of which projects wire the analyzer.
public sealed class FallbackMarker;
