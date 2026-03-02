using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Prognosis.Generators;

/// <summary>
/// Incremental source generator that scans for node-defining invocations:
/// <c>HealthNode.Create("name")</c>,
/// <c>HealthNode.CreateDelegate("name", ...)</c>,
/// <c>HealthNode.CreateComposite("name")</c>,
/// <c>PrognosisBuilder.AddComposite("name", ...)</c>,
/// <c>PrognosisBuilder.AddProbe("name", ...)</c>, and
/// <c>PrognosisBuilder.AddDelegate("name", ...)</c>.
/// Extracts the string literal name argument and emits a <c>HealthNames</c>
/// class containing <see langword="const"/> <see langword="string"/> fields
/// for each discovered name.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HealthNodeNameCollector : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsHealthNodeFactoryCandidate(node),
                transform: static (ctx, _) => ExtractNodeName(ctx))
            .Where(static name => name is not null)
            .Collect();

        context.RegisterSourceOutput(names, static (spc, collected) =>
        {
            var distinct = collected
                .Where(n => n is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToImmutableArray();

            if (distinct.Length == 0)
                return;

            spc.AddSource("HealthNames.g.cs", SourceText.From(GenerateSource(distinct), Encoding.UTF8));
        });
    }

    /// <summary>
    /// Fast syntactic filter — returns <see langword="true"/> for
    /// invocation expressions whose method name is <c>Create</c>,
    /// <c>CreateDelegate</c>, <c>CreateComposite</c>, <c>AddComposite</c>,
    /// <c>AddProbe</c>, or <c>AddDelegate</c> with at least one argument.
    /// </summary>
    private static bool IsHealthNodeFactoryCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return false;

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null
        };

        return name is "Create" or "CreateDelegate" or "CreateComposite" or "AddComposite" or "AddProbe" or "AddDelegate"
            && invocation.ArgumentList.Arguments.Count > 0;
    }

    /// <summary>
    /// Semantic transform — verifies the invocation targets
    /// <c>Prognosis.HealthNode</c> or <c>Prognosis.DependencyInjection.PrognosisBuilder</c>
    /// and extracts the first string argument if it is a literal or const.
    /// Falls back to syntactic extraction for builder methods when the
    /// semantic model cannot resolve the symbol (e.g., due to errors in the
    /// same method body from referencing the not-yet-generated HealthNames).
    /// </summary>
    private static string? ExtractNodeName(GeneratorSyntaxContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null
        };

        if (ctx.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
        {
            var isHealthNode = method.ContainingType is { Name: "HealthNode" }
                && method.ContainingType.ContainingNamespace?.ToString() == "Prognosis"
                && method.Name is "Create" or "CreateDelegate" or "CreateComposite";

            var isBuilder = method.ContainingType is { Name: "PrognosisBuilder" }
                && method.ContainingType.ContainingNamespace?.ToString() == "Prognosis.DependencyInjection"
                && method.Name is "AddComposite" or "AddProbe" or "AddDelegate";

            if (!isHealthNode && !isBuilder)
                return null;
        }
        else if (methodName is not ("AddComposite" or "AddProbe" or "AddDelegate"
                                 or "Create" or "CreateDelegate" or "CreateComposite"))
        {
            return null;
        }

        // Extract the first string literal or const argument.
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            // Fast path: string literal — works even when the semantic model has errors.
            if (arg.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText is { Length: > 0 } literalValue)
                return literalValue;

            var constant = ctx.SemanticModel.GetConstantValue(arg.Expression);
            if (constant.HasValue && constant.Value is string name && name.Length > 0)
                return name;
        }

        return null;
    }

    private static string GenerateSource(ImmutableArray<string> names)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Prognosis;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Auto-generated constants for every health node name discovered in this");
        sb.AppendLine("/// compilation via <c>HealthNode.Create</c>, <c>CreateDelegate</c>,");
        sb.AppendLine("/// <c>CreateComposite</c>, <c>PrognosisBuilder.AddComposite</c>,");
        sb.AppendLine("/// <c>AddProbe</c>, and <c>AddDelegate</c> calls.");
        sb.AppendLine("/// Use these constants in place of string literals for refactoring safety,");
        sb.AppendLine("/// autocomplete, and find-all-references support.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class HealthNames");
        sb.AppendLine("{");

        foreach (var name in names)
        {
            var fieldName = SanitizeFieldName(name);
            sb.AppendLine($"    /// <summary>Node name: <c>\"{EscapeXml(name)}\"</c></summary>");
            sb.AppendLine($"    public const string {fieldName} = \"{EscapeString(name)}\";");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Converts a node name like <c>"Database.Connection"</c> to a valid
    /// C# identifier like <c>Database_Connection</c>.
    /// </summary>
    internal static string SanitizeFieldName(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '.' || c == '-' || c == ' ' || c == '/')
                sb.Append('_');
            else if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            // Skip other characters.
        }

        // Ensure the identifier doesn't start with a digit.
        if (sb.Length > 0 && char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.Length > 0 ? sb.ToString() : "_Unknown";
    }

    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
