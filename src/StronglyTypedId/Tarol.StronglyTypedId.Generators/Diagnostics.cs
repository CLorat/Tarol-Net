using Microsoft.CodeAnalysis;

namespace Tarol.StronglyTypedId.Generators;

internal static class Diagnostics
{
    private const string Category = "Tarol.StronglyTypedId";

    /// <summary>TSID001: a spelled Id&lt;X&gt; cannot be registered because generated code cannot reference X.</summary>
    public static readonly DiagnosticDescriptor MarkerNotRegistered = new(
        id: "TSID001",
        title: "Strongly-typed id marker cannot be registered",
        messageFormat: "Id<{0}> gets no TypeConverter registration because {1}; configuration and MVC binding of this id will not work. Make the marker internal or public, or call IdConverterRegistry.Register<T>() yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "JSON serialization is unaffected. Only TypeDescriptor-based binding needs the registration.");
}
