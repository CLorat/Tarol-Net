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
    public void A_module_lists_the_assemblys_json_aggregates_reactions_and_jobs()
        => Snapshot.Match("module", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Reactions, Sources.Jobs, Sources.Module)));

    [Fact]
    public void An_assembly_without_the_module_attribute_gets_no_module()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Reactions)).GetRunResult().GeneratedTrees
            .ShouldNotContain(t => t.FilePath.EndsWith("GeneratedModule.g.cs", StringComparison.Ordinal));

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

    [Fact]
    public void Jobs_get_one_public_set_per_assembly()
        => Snapshot.Match("jobs", GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Jobs)));

    [Fact]
    public void The_generated_jobs_compile_without_warnings()
        => string.Join("\n", GeneratorTestHost.Diagnose(Sources.Jobs).Select(d => d.ToString())).ShouldBeEmpty();

    [Fact]
    public void An_assembly_without_jobs_gets_no_job_set()
        => GeneratorTestHost.Run(GeneratorTestHost.Compile(GeneratorTestHost.Domain, Sources.Reactions)).GetRunResult().GeneratedTrees
            .ShouldNotContain(t => t.FilePath.EndsWith("GeneratedJobs.g.cs", StringComparison.Ordinal));
}
