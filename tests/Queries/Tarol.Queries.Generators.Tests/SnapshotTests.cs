using Tarol.Testing.Generators;

namespace Tarol.Queries.Generators.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void Projections_get_their_switch_and_column_map()
        => Snapshot.Match("projections", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Projections)));

    [Fact]
    public void The_generated_code_compiles_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Projections).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_assembly_without_projections_gets_nothing()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain)).GetRunResult().GeneratedTrees.ShouldBeEmpty();
}
