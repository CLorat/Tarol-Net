using Tarol.Testing.Generators;

namespace Tarol.Primitives.Generators.Tests;

public class SnapshotTests
{
    [Theory]
    [InlineData("Gallery", Domains.Gallery)]
    [InlineData("Orders", Domains.Orders)]
    [InlineData("Nested", Domains.Nested)]
    [InlineData("Lifecycle", Domains.Lifecycle)]
    public void Generated_Code_Matches_The_Snapshot(string name, string source)
        => Snapshot.Match(name, GeneratorTestHost.Run(GeneratorTestHost.Compile(source)));

    [Theory]
    [InlineData(Domains.Gallery)]
    [InlineData(Domains.Orders)]
    [InlineData(Domains.Nested)]
    [InlineData(Domains.Lifecycle)]
    public void A_Valid_Domain_Compiles_With_No_Diagnostic(string source)
        => GeneratorTestHost.Diagnose(source).Select(static d => d.ToString()).ShouldBeEmpty();
}
