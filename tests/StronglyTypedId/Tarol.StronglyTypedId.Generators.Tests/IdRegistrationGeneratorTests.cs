using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tarol.StronglyTypedId.Generators.Tests;

public class IdRegistrationGeneratorTests
{
    [Fact]
    public void Emits_Registration_For_Concrete_Id_Usage()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;

                                                              namespace App
                                                              {
                                                                  public class User { }
                                                                  public class UserDto { public Id<User> Id { get; set; } }
                                                              }
                                                              """);

        var generated = GeneratorTestHost.Run(compilation).SingleGeneratedSource();

        generated.ShouldContain("System.Runtime.CompilerServices.ModuleInitializer");
        generated.ShouldContain("IdConverterRegistry.Register<global::App.User>();");
    }

    [Fact]
    public void Emits_Registration_For_The_External_Assembly_Snippet_With_Interface_Marker()
    {
        // The user's exact real-world shape: an interface marker used from a class
        // property in a separate library.
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              namespace Tarol.StronglyTypedId.Generators.Utility;

                                                              public interface IExternalAssemblyTestClassWithId
                                                              {
                                                              }

                                                              public class ExternalAssemblyTestClassWithId
                                                              {
                                                                  public Id<IExternalAssemblyTestClassWithId> Id { get; set; }
                                                              }
                                                              """);

        var generated = GeneratorTestHost.Run(compilation).SingleGeneratedSource();

        generated.ShouldContain(
            "Register<global::Tarol.StronglyTypedId.Generators.Utility.IExternalAssemblyTestClassWithId>();");
    }

    [Fact]
    public void Deduplicates_Repeated_Usages_Of_The_Same_Marker()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;
                                                              using System.Collections.Generic;

                                                              namespace App
                                                              {
                                                                  public class User { }
                                                                  public class A { public Id<User> X { get; set; } }
                                                                  public class B { public List<Id<User>> Y { get; set; } }
                                                                  public class C { public Dictionary<Id<User>, string> Z { get; set; } }
                                                              }
                                                              """);

        var generated = GeneratorTestHost.Run(compilation).SingleGeneratedSource();

        CountOccurrences(generated, "Register<global::App.User>();").ShouldBe(1);
    }

    [Fact]
    public void Output_Is_Deterministically_Sorted_Regardless_Of_Source_Order()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;

                                                              namespace App
                                                              {
                                                                  public class Zebra { }
                                                                  public class Alpha { }
                                                                  // Declared Zebra-first on purpose:
                                                                  public class Dto
                                                                  {
                                                                      public Id<Zebra> Z { get; set; }
                                                                      public Id<Alpha> A { get; set; }
                                                                  }
                                                              }
                                                              """);

        var generated = GeneratorTestHost.Run(compilation).SingleGeneratedSource();

        generated.IndexOf("global::App.Alpha", StringComparison.Ordinal)
            .ShouldBeLessThan(generated.IndexOf("global::App.Zebra", StringComparison.Ordinal));
    }

    [Fact]
    public void Skips_Open_Generic_Usages()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;

                                                              namespace App
                                                              {
                                                                  public class Repository<T>
                                                                  {
                                                                      public Id<T> Current { get; set; }       // open: skip
                                                                  }
                                                                  public class Wrapper<T> { }
                                                                  public class Holder<T>
                                                                  {
                                                                      public Id<Wrapper<T>> Nested { get; set; } // nested type parameter: skip
                                                                  }
                                                              }
                                                              """);

        GeneratorTestHost.Run(compilation).GetRunResult()
            .Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Skips_Private_Nested_Markers_That_Generated_Code_Cannot_Reference()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;

                                                              namespace App
                                                              {
                                                                  public class Outer
                                                                  {
                                                                      private class Secret { }
                                                                      private Id<Secret> _hidden;
                                                                  }
                                                              }
                                                              """);

        GeneratorTestHost.Run(compilation).GetRunResult()
            .Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Internal_Markers_Are_Registered_Because_Generation_Is_Per_Assembly()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;

                                                              namespace App
                                                              {
                                                                  internal class InternalUser { }
                                                                  internal class Dto { public Id<InternalUser> Id { get; set; } }
                                                              }
                                                              """);

        GeneratorTestHost.Run(compilation).SingleGeneratedSource()
            .ShouldContain("Register<global::App.InternalUser>();");
    }

    [Fact]
    public void Emits_Nothing_When_No_Id_Usages_Exist()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              namespace App { public class NothingHere { } }
                                                              """);

        GeneratorTestHost.Run(compilation).GetRunResult()
            .Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_Unrelated_Types_Also_Named_Id()
    {
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              namespace App
                                                              {
                                                                  public class Id<T> { }                 // impostor in the wrong namespace
                                                                  public class Dto { public Id<Dto> X { get; set; } }
                                                              }
                                                              """);

        GeneratorTestHost.Run(compilation).GetRunResult()
            .Results.Single().GeneratedSources.ShouldBeEmpty();
    }

    [Fact]
    public void Pipeline_Output_Is_Cached_When_An_Unrelated_File_Is_Added()
    {
        // THE regression test for incrementality — this is the test that fails
        // instantly if anyone reintroduces a Combine(CompilationProvider) or breaks
        // the EquatableArray comparison, turning the generator back into an
        // every-keystroke full scan.
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;
                                                              namespace App
                                                              {
                                                                  public class User { }
                                                                  public class Dto { public Id<User> Id { get; set; } }
                                                              }
                                                              """);

        var driver = GeneratorTestHost.Run(compilation);

        var editedCompilation = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace App { public class Unrelated { } }",
                cancellationToken: TestContext.Current.CancellationToken));

        driver = driver.RunGenerators(editedCompilation, TestContext.Current.CancellationToken);

        var outputReasons = driver.GetRunResult().Results.Single()
            .TrackedOutputSteps
            .SelectMany(static step => step.Value)
            .SelectMany(static step => step.Outputs)
            .Select(static output => output.Reason)
            .ToList();

        outputReasons.ShouldNotBeEmpty();
        outputReasons.ShouldAllBe(static reason =>
            reason == IncrementalStepRunReason.Cached ||
            reason == IncrementalStepRunReason.Unchanged);
    }

    [Fact]
    public void Generated_Class_Name_Includes_Sanitized_Assembly_Name()
    {
        // Prevents internal-type name ambiguity when two libraries with
        // InternalsVisibleTo relationships both run the generator.
        var compilation = GeneratorTestHost.CreateCompilation("""
                                                              using Tarol.StronglyTypedId;
                                                              namespace App
                                                              {
                                                                  public class User { }
                                                                  public class Dto { public Id<User> Id { get; set; } }
                                                              }
                                                              """);

        // Host assembly name is "GeneratorTestAssembly" — letters only, sanitizes to itself.
        GeneratorTestHost.Run(compilation).SingleGeneratedSource()
            .ShouldContain("class StronglyTypedIdRegistrations_GeneratorTestAssembly");
    }

    [Fact]
    public void Two_Libraries_Sharing_A_Marker_Each_Emit_Their_Own_Registration()
    {
        // Simulates LibA and LibB both using Id<SharedModels.User>: each assembly's
        // generator run is independent, each emits its own module initializer, and
        // both register the same marker — which the runtime registry makes a no-op
        // (verified by IdJsonSerializationTests.Registry_Fast_Path and
        // IdTypeConverterTests.Double_Registration).
        const string sharedUsage = """
                                   using Tarol.StronglyTypedId;
                                   namespace SharedModels { public class User { } }
                                   namespace Lib { public class Dto { public Id<SharedModels.User> Id { get; set; } } }
                                   """;

        var libA = GeneratorTestHost.Run(GeneratorTestHost.CreateCompilation(sharedUsage, "LibA"));
        var libB = GeneratorTestHost.Run(GeneratorTestHost.CreateCompilation(sharedUsage, "LibB"));

        var sourceA = libA.SingleGeneratedSource();
        var sourceB = libB.SingleGeneratedSource();

        sourceA.ShouldContain("Register<global::SharedModels.User>();");
        sourceB.ShouldContain("Register<global::SharedModels.User>();");

        // Distinct class identities per assembly — no cross-assembly type conflict
        // even under InternalsVisibleTo.
        sourceA.ShouldContain("StronglyTypedIdRegistrations_LibA");
        sourceB.ShouldContain("StronglyTypedIdRegistrations_LibB");
    }

    [Fact]
    public void Assembly_Names_With_Dots_Sanitize_To_Valid_Identifiers()
    {
        var compilation = GeneratorTestHost.CreateCompilation(
            """
            using Tarol.StronglyTypedId;
            namespace App { public class User { public Id<User> Id { get; set; } } }
            """,
            "My.Company.Orders-v2");

        GeneratorTestHost.Run(compilation).SingleGeneratedSource()
            .ShouldContain("StronglyTypedIdRegistrations_My_Company_Orders_v2");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
