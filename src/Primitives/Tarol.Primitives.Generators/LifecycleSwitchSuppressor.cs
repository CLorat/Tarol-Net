using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Primitives.Generators;

/// <summary>
/// TPRMSPR01: suppresses CS8524 ("an unnamed enum value is not handled") on a <c>switch</c> whose governing expression is a
/// <c>[Lifecycle]</c> enum. A lifecycle has exactly its named states, so a switch that names every state is complete
/// without a <c>_</c> arm; a missing state stays CS8509. Switches over any other enum keep CS8524.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LifecycleSwitchSuppressor : DiagnosticSuppressor
{
    internal static readonly SuppressionDescriptor Rule = new(
        "TPRMSPR01", "CS8524",
        "A [Lifecycle] enum has only its named states; a switch that covers them all is exhaustive.");

    /// <inheritdoc />
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree)
                continue;
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var governing = node.FirstAncestorOrSelf<SwitchExpressionSyntax>()?.GoverningExpression
                            ?? node.FirstAncestorOrSelf<SwitchStatementSyntax>()?.Expression;
            if (governing is null)
                continue;
            var type = context.GetSemanticModel(tree).GetTypeInfo(governing, context.CancellationToken).Type;
            if (LifecycleDiscovery.IsLifecycle(type))
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
        }
    }
}
