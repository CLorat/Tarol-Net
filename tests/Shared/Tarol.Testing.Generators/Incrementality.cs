using Microsoft.CodeAnalysis;

namespace Tarol.Testing.Generators;

/// <summary>Assertions for incremental-generator caching.</summary>
public static class Incrementality
{
    /// <summary>
    ///     Returns the run reasons of every source output of the last run that are neither <c>Cached</c> nor
    ///     <c>Unchanged</c>. An empty list means the edit regenerated nothing. The driver must have been created with
    ///     <c>trackIncrementalGeneratorSteps: true</c>.
    /// </summary>
    public static IReadOnlyList<string> RegeneratedOutputs(GeneratorDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return
        [
            .. driver.GetRunResult().Results
                .SelectMany(r => r.TrackedOutputSteps)
                .SelectMany(step => step.Value.SelectMany(run => run.Outputs.Select(o => (step.Key, o.Reason))))
                .Where(x => x.Reason is not (IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged))
                .Select(x => $"{x.Key}: {x.Reason}")
        ];
    }
}
