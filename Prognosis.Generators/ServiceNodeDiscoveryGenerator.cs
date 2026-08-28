using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Prognosis.Generators;

/// <summary>
/// Incremental source generator that scans for classes with public
/// <c>HealthNode</c> properties, reads <c>[DependsOn]</c> attributes
/// on those properties, and emits an <c>AddDiscoveredNodes()</c>
/// extension method on <c>PrognosisBuilder</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ServiceNodeDiscoveryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var nodes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsHealthNodePropertyCandidate(node),
                transform: static (ctx, _) => ExtractServiceNode(ctx))
            .Where(static info => info is not null)
            .Collect();

        var nodesWithCompilation = nodes.Combine(context.CompilationProvider);

        context.RegisterSourceOutput(nodesWithCompilation, static (spc, pair) =>
        {
            var (collected, compilation) = pair;

            // Only emit AddDiscoveredNodes when PrognosisBuilder is referenceable.
            if (compilation.GetTypeByMetadataName("Prognosis.DependencyInjection.PrognosisBuilder") is null)
                return;

            var entries = collected
                .Where(e => e is not null)
                .Cast<ServiceNodeEntry>()
                .ToImmutableArray();

            if (entries.Length == 0)
                return;

            spc.AddSource(
                "AddDiscoveredNodes.g.cs",
                SourceText.From(GenerateExtension(entries), Encoding.UTF8));
        });
    }

    private static bool IsHealthNodePropertyCandidate(SyntaxNode node)
    {
        return node is PropertyDeclarationSyntax prop
            && prop.Type is IdentifierNameSyntax { Identifier.Text: "HealthNode" }
               or QualifiedNameSyntax { Right.Identifier.Text: "HealthNode" };
    }

    private static ServiceNodeEntry? ExtractServiceNode(GeneratorSyntaxContext ctx)
    {
        var prop = (PropertyDeclarationSyntax)ctx.Node;

        if (ctx.SemanticModel.GetDeclaredSymbol(prop) is not IPropertySymbol propSymbol)
            return null;

        if (propSymbol.Type is not { Name: "HealthNode" }
            || propSymbol.Type.ContainingNamespace?.ToString() != "Prognosis")
            return null;

        if (propSymbol.DeclaredAccessibility != Accessibility.Public)
            return null;

        var containingType = propSymbol.ContainingType;
        if (containingType is null || containingType.IsAbstract || containingType.IsStatic)
            return null;

        var fullTypeName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var propertyName = propSymbol.Name;

        var deps = ImmutableArray.CreateBuilder<DependencyEntry>();
        foreach (var attr in propSymbol.GetAttributes())
        {
            if (attr.AttributeClass is not { Name: "DependsOnAttribute" }
                || attr.AttributeClass.ContainingNamespace?.ToString() != "Prognosis.DependencyInjection")
                continue;

            if (attr.ConstructorArguments.Length < 1)
                continue;

            var depName = attr.ConstructorArguments[0].Value as string;
            if (string.IsNullOrEmpty(depName))
                continue;

            // Omitted arg => the attribute's own default. Anything else that cannot be mapped
            // emits uncompilable source rather than a guess: a generator cannot fail its own
            // build, and a silently mis-weighted edge is worse than a visible break (ADR-008).
            var importance = "Importance.Required";
            if (attr.ConstructorArguments.Length >= 2)
            {
                // Positional map onto Importance's declaration order; keep in lockstep.
                importance = attr.ConstructorArguments[1].Value is int impVal
                    ? impVal switch
                    {
                        0 => "Importance.Required",
                        1 => "Importance.Important",
                        2 => "Importance.Optional",
                        3 => "Importance.Resilient",
                        4 => "Importance.Advisory",
                        _ => $"Importance.__UnmappedImportanceValue_{impVal}__SeeAdr008",
                    }
                    // Defensive only: a type-mismatched argument makes Roslyn drop
                    // ConstructorArguments entirely, so this is not reached that way.
                    : "Importance.__MalformedImportanceArgument__SeeAdr008";
            }

            deps.Add(new DependencyEntry(depName!, importance));
        }

        return new ServiceNodeEntry(fullTypeName, propertyName, deps.ToImmutable());
    }

    private static string GenerateExtension(ImmutableArray<ServiceNodeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLineLf("// <auto-generated />");
        sb.AppendLineLf("#nullable enable");
        sb.AppendLineLf();
        sb.AppendLineLf("namespace Prognosis.DependencyInjection;");
        sb.AppendLineLf();
        sb.AppendLineLf("/// <summary>");
        sb.AppendLineLf("/// Auto-generated extension that registers all discovered service nodes");
        sb.AppendLineLf("/// (classes with public <see cref=\"Prognosis.HealthNode\"/> properties)");
        sb.AppendLineLf("/// and their <c>[DependsOn]</c>-declared edges.");
        sb.AppendLineLf("/// </summary>");
        sb.AppendLineLf("public static class PrognosisBuilderDiscoveryExtensions");
        sb.AppendLineLf("{");
        sb.AppendLineLf("    /// <summary>");
        sb.AppendLineLf("    /// Registers all <see cref=\"Prognosis.HealthNode\"/> properties discovered");
        sb.AppendLineLf("    /// in this compilation via <see cref=\"PrognosisBuilder.AddServiceNode{TService}\"/>.");
        sb.AppendLineLf("    /// </summary>");
        sb.AppendLineLf("    public static PrognosisBuilder AddDiscoveredNodes(this PrognosisBuilder builder)");
        sb.AppendLineLf("    {");

        foreach (var entry in entries)
        {
            if (entry.Dependencies.Length == 0)
            {
                sb.AppendLineLf($"        builder.AddServiceNode<{entry.FullTypeName}>(svc => svc.{entry.PropertyName});");
            }
            else
            {
                sb.AppendLineLf($"        builder.AddServiceNode<{entry.FullTypeName}>(svc => svc.{entry.PropertyName}, deps =>");
                sb.AppendLineLf("        {");
                foreach (var dep in entry.Dependencies)
                {
                    sb.AppendLineLf($"            deps.DependsOn(\"{EscapeString(dep.NodeName)}\", {dep.Importance});");
                }
                sb.AppendLineLf("        });");
            }
        }

        sb.AppendLineLf("        return builder;");
        sb.AppendLineLf("    }");
        sb.AppendLineLf("}");
        return sb.ToString();
    }

    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class ServiceNodeEntry
    {
        public string FullTypeName { get; }
        public string PropertyName { get; }
        public ImmutableArray<DependencyEntry> Dependencies { get; }

        public ServiceNodeEntry(string fullTypeName, string propertyName, ImmutableArray<DependencyEntry> dependencies)
        {
            FullTypeName = fullTypeName;
            PropertyName = propertyName;
            Dependencies = dependencies;
        }
    }

    private sealed class DependencyEntry
    {
        public string NodeName { get; }
        public string Importance { get; }

        public DependencyEntry(string nodeName, string importance)
        {
            NodeName = nodeName;
            Importance = importance;
        }
    }
}
