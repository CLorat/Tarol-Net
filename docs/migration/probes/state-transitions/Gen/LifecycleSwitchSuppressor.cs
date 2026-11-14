using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tarol.Lifecycle.Generators;

/// <summary>
/// CS8524 ("unnamed enum value not covered") on a switch over a [Lifecycle] enum: a lifecycle has exactly its named
/// states, so a switch that covers them is complete. CS8509 (a named state missing) stays.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LifecycleSwitchSuppressor : DiagnosticSuppressor
{
    internal static readonly SuppressionDescriptor Rule = new("TPRMSPR01", "CS8524",
        "A [Lifecycle] enum has only its named states; a switch that covers them all is exhaustive.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => ImmutableArray.Create(Rule);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree is null)
                continue;
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var governing = node.FirstAncestorOrSelf<SwitchExpressionSyntax>()?.GoverningExpression
                            ?? node.FirstAncestorOrSelf<SwitchStatementSyntax>()?.Expression;
            if (governing is null)
                continue;
            var type = context.GetSemanticModel(tree).GetTypeInfo(governing, context.CancellationToken).Type;
            if (type?.TypeKind == TypeKind.Enum
                && type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "Tarol.Primitives.LifecycleAttribute"))
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
        }
    }
}
