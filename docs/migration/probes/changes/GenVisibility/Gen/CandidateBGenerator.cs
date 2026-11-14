using Microsoft.CodeAnalysis;

namespace Probe.Changes.CandidateB;

// Candidate B: [Change] on a behaviour method; the generator writes the input record from its parameters.
// Here: the record the generator would emit for Auction.Rename([Change] ... Title title), with the rule on the value
// object, the IValidatable marker and a partial declaration, i.e. exactly what Tarol.Validations generates Validate for.
[Generator(LanguageNames.CSharp)]
public sealed class CandidateBGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
        // A real candidate B derives the record from the user's method, so it must be a source output (not post-init):
        // post-initialization output is the one generated source other generators do see.
        => context.RegisterSourceOutput(context.CompilationProvider, static (c, _) => c.AddSource("RenameAuction.g.cs", """
            namespace Probe;
            public sealed partial record RenameAuction(global::Tarol.StronglyTypedId.Id<Auction> Auction, Title Title)
                : global::Tarol.Validations.IValidatable;
            """));
}
