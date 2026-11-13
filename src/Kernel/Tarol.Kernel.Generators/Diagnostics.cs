using Microsoft.CodeAnalysis;

namespace Tarol.Kernel.Generators;

internal static class Diagnostics
{
    /// <summary>TKRN001: the system clock is read directly instead of through an injected TimeProvider.</summary>
    public static readonly DiagnosticDescriptor SystemClock = new(
        id: "TKRN001",
        title: "System clock read directly",
        messageFormat: "'{0}' reads the system clock. Inject TimeProvider (or take 'now' as a parameter) so that tests control time.",
        category: "Tarol.Kernel",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Tarol has no ambient clock (docs/architecture/time.md). Code with a container takes a TimeProvider; an aggregate behaviour takes 'DateTimeOffset now' from its handler; tests pass a FakeTimeProvider. TimeProvider.System is fine where the provider is composed (AddSingleton(TimeProvider.System)). Downgrade the rule in .editorconfig where real time is intended.");
}
