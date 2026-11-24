namespace Tarol.Changes;

/// <summary>
/// Generates <c>GeneratedModule.Module</c> for this assembly (TN-8): a <see cref="Tarol.Persistence.TarolModule"/> with the
/// assembly's JSON contexts, aggregates, reactions, jobs, projections, HTTP entries and OpenAPI transformers, for
/// <c>services.AddTarol(moduleA, moduleB)</c> in an application made of several assemblies.
/// </summary>
/// <example><c>[assembly: GenerateTarolModule("Datenschutz")]</c></example>
/// <param name="name">The module's name; absent: the assembly's name.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GenerateTarolModuleAttribute(string? name = null) : Attribute
{
    /// <summary>The module's name, or <see langword="null"/> for the assembly's.</summary>
    public string? Name { get; } = name;
}
