using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Tarol.Messaging.Generators;

/// <summary>One [Consumes] factory, symbol-free; the location does not take part in equality (a moved declaration regenerates nothing).</summary>
internal sealed record ConsumerModel(string Name, string Contract, int Version, string Payload, string Change, string Method, bool TakesMessage,
    LocationInfo? Location)
{
    public bool Equals(ConsumerModel? other) => other is not null && Name == other.Name && Contract == other.Contract && Version == other.Version
        && Payload == other.Payload && Change == other.Change && Method == other.Method && TakesMessage == other.TakesMessage;

    public override int GetHashCode() => (Name, Contract, Version, Change).GetHashCode();
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
        => new(descriptor.Id, location, new EquatableArray<string>(arguments.ToImmutableArray()));

    public Diagnostic ToDiagnostic()
    {
        var args = new object[Arguments.Count];
        var i = 0;
        foreach (var argument in Arguments)
            args[i++] = argument;
        return Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(), args);
    }
}

internal sealed record ConsumerResult(ConsumerModel? Model, DiagnosticInfo? Diagnostic);
