using Tarol.Testing.Generators;

namespace Tarol.Changes.Generators.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void Changes_get_their_target_reads_and_one_dispatcher()
        => Snapshot.Match("changes", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Changes)));

    [Fact]
    public void The_generated_code_compiles_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Changes).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_assembly_without_changes_gets_no_dispatcher()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain)).GetRunResult().GeneratedTrees.ShouldBeEmpty();

    [Fact]
    public void Reactions_get_one_public_set_per_assembly()
        => Snapshot.Match("reactions", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Reactions)));

    [Fact]
    public void The_generated_reactions_compile_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Reactions).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_assembly_without_reactions_gets_no_reaction_set()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Changes)).GetRunResult().GeneratedTrees
            .ShouldNotContain(t => t.FilePath.EndsWith("GeneratedReactions.g.cs", StringComparison.Ordinal));
}
