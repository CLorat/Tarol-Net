using System.ComponentModel;

namespace Tarol.Changes;

/// <summary>
/// Written by the generator next to <c>GeneratedReactions</c> and <c>GeneratedJobs</c>: the reaction or job names an assembly
/// declares. The generator of an assembly that references several such assemblies (the host) reports a name declared in two
/// of them at build time (TCHG012), instead of <see cref="ReactionSet.Combine"/> refusing at start (Tatalu FB-INT3-1).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DeclaredNamesAttribute : Attribute
{
    /// <summary>Records the names.</summary>
    /// <param name="kind"><c>reaction</c> or <c>job</c>.</param>
    /// <param name="names">The names, as stored in outbox and job rows.</param>
    public DeclaredNamesAttribute(string kind, params string[] names)
    {
        Kind = kind;
        Names = names;
    }

    /// <summary><c>reaction</c> or <c>job</c>.</summary>
    public string Kind { get; }

    /// <summary>The names.</summary>
    public IReadOnlyList<string> Names { get; }
}
