using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Prognosis.Generators;

/// <summary>
/// Diagnostic analyzer that validates string-based node name references
/// against the set of names discovered from
/// <c>HealthNode.Create("name")</c> and
/// <c>PrognosisBuilder.AddNode("name")</c>
/// calls in the same compilation.
/// Reports <c>PROGNOSIS001</c> when a referenced name doesn't match any
/// known node.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DependsOnEdgeAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic ID for an unknown node name reference.</summary>
    public const string DiagnosticId = "PROGNOSIS001";

    private static readonly DiagnosticDescriptor s_unknownNodeRule = new(
        id: DiagnosticId,
        title: "Unknown health node name",
        messageFormat: "Node name '{0}' does not match any HealthNode.Create or PrognosisBuilder.AddNode call in this compilation",
        category: "Prognosis",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "The referenced node name was not found among names passed to " +
            "HealthNode.Create or PrognosisBuilder.AddNode in the " +
            "current compilation. This may cause a runtime KeyNotFoundException " +
            "when the graph is materialized.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(s_unknownNodeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            var knownNames = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            var pendingReferences = new ConcurrentBag<PendingReference>();

            // Both phases run as SyntaxNodeActions (avoids GetSemanticModel at compilation end).
            compilationContext.RegisterSyntaxNodeAction(ctx =>
            {
                var invocation = (InvocationExpressionSyntax)ctx.Node;
                CollectNodeName(ctx, invocation, knownNames);
                CollectReference(ctx, invocation, pendingReferences);
            }, SyntaxKind.InvocationExpression);

            // At compilation end, cross-reference collected names and references.
            compilationContext.RegisterCompilationEndAction(endCtx =>
            {
                foreach (var reference in pendingReferences)
                {
                    if (!knownNames.ContainsKey(reference.Name))
                    {
                        endCtx.ReportDiagnostic(Diagnostic.Create(
                            s_unknownNodeRule,
                            reference.Location,
                            reference.Name));
                    }
                }
            });
        });
    }

    private static void CollectNodeName(
        SyntaxNodeAnalysisContext ctx,
        InvocationExpressionSyntax invocation,
        ConcurrentDictionary<string, bool> knownNames)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;
        if (methodName is not ("Create" or "CreateDelegate" or "CreateComposite" or "AddNode"))
            return;

        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
            is not IMethodSymbol method)
            return;

        var isHealthNode = method.ContainingType is { Name: "HealthNode" }
            && method.ContainingType.ContainingNamespace?.ToString() == "Prognosis"
            && method.Name is "Create" or "CreateDelegate" or "CreateComposite";

        var isBuilder = method.ContainingType is { Name: "PrognosisBuilder" }
            && method.ContainingType.ContainingNamespace?.ToString() == "Prognosis.DependencyInjection"
            && method.Name is "AddNode";

        if (!isHealthNode && !isBuilder)
            return;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var constant = ctx.SemanticModel.GetConstantValue(arg.Expression, ctx.CancellationToken);
            if (constant.HasValue && constant.Value is string name && name.Length > 0)
            {
                knownNames.TryAdd(name, true);
                return;
            }
        }
    }

    private static void CollectReference(
        SyntaxNodeAnalysisContext ctx,
        InvocationExpressionSyntax invocation,
        ConcurrentBag<PendingReference> pendingReferences)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        if (memberAccess.Name.Identifier.Text != "DependsOn")
            return;

        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
            is not IMethodSymbol method)
            return;

        if (!IsDependencyConfiguratorDependsOn(method))
            return;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        var constant = ctx.SemanticModel.GetConstantValue(firstArg, ctx.CancellationToken);
        if (!constant.HasValue || constant.Value is not string referencedName)
            return;

        if (referencedName.Length > 0)
            pendingReferences.Add(new PendingReference(referencedName, firstArg.GetLocation()));
    }

    /// <summary>
    /// Matches <c>Prognosis.DependencyInjection.DependencyConfigurator.DependsOn(string, Importance)</c>
    /// or <c>Prognosis.DependencyInjection.NodeConfigurator.DependsOn(string, Importance)</c>.
    /// </summary>
    private static bool IsDependencyConfiguratorDependsOn(IMethodSymbol method)
    {
        if (method.Name != "DependsOn")
            return false;

        var containingType = method.ContainingType;
        if (containingType is null)
            return false;

        if (containingType.Name is "DependencyConfigurator" or "NodeConfigurator"
            && containingType.ContainingNamespace?.ToString() == "Prognosis.DependencyInjection")
        {
            return method.Parameters.Length >= 1
                && method.Parameters[0].Type.SpecialType == SpecialType.System_String;
        }

        return false;
    }

    private readonly struct PendingReference
    {
        public readonly string Name;
        public readonly Location Location;

        public PendingReference(string name, Location location)
        {
            Name = name;
            Location = location;
        }
    }
}
