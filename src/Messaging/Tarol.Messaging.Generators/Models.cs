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

/// <summary>One [Publishes&lt;T&gt;] method, symbol-free; the location does not take part in equality.</summary>
internal sealed record PublicationModel(string Contract, int Version, string Event, EquatableArray<string> EventBases, string Record, string Type,
    string Method, bool TakesRecorded, LocationInfo? Location)
{
    public string Name => "publish:" + Contract + "@" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The simple name of the aggregate whose stream the event belongs to, if known statically (BC-25).</summary>
    public string? Stream { get; init; }

    public bool Equals(PublicationModel? other) => other is not null && Contract == other.Contract && Version == other.Version && Event == other.Event
        && EventBases.Equals(other.EventBases) && Record == other.Record && Type == other.Type && Method == other.Method && TakesRecorded == other.TakesRecorded
        && Stream == other.Stream;

    public override int GetHashCode() => (Contract, Version, Event, Method).GetHashCode();
}

internal sealed record PublicationResult(PublicationModel? Model, DiagnosticInfo? Diagnostic);

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments, bool Warning = false)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
        => new(descriptor.Id, location, new EquatableArray<string>(arguments.ToImmutableArray()));

    /// <summary>A diagnostic of an Error rule reported as a warning (TMSG005's two prefixes).</summary>
    public static DiagnosticInfo AsWarning(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
        => new(descriptor.Id, location, new EquatableArray<string>(arguments.ToImmutableArray()), Warning: true);

    public Diagnostic ToDiagnostic()
    {
        var args = new object[Arguments.Count];
        var i = 0;
        foreach (var argument in Arguments)
            args[i++] = argument;
        var descriptor = Diagnostics.ById(Id);
        return Warning
            ? Diagnostic.Create(descriptor, Location?.ToLocation(), DiagnosticSeverity.Warning, additionalLocations: null, properties: null, args)
            : Diagnostic.Create(descriptor, Location?.ToLocation(), args);
    }
}

internal sealed record ConsumerResult(ConsumerModel? Model, DiagnosticInfo? Diagnostic);
