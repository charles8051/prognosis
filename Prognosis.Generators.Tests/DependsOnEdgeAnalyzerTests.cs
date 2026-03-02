using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Prognosis.Generators;
using Xunit;

namespace Prognosis.Generators.Tests;

public class DependsOnEdgeAnalyzerTests
{
    /// <summary>
    /// Stubs for HealthNode + DependencyConfigurator so the semantic model
    /// can resolve methods without referencing the real Prognosis assemblies.
    /// </summary>
    private const string Stubs = """
        namespace Prognosis
        {
            public enum HealthStatus { Healthy, Unhealthy }
            public class HealthEvaluation
            {
                public HealthStatus Status { get; }
            }
            public sealed class HealthNode
            {
                public static HealthNode Create(string name) => new HealthNode();
                public static HealthNode CreateDelegate(string name) => new HealthNode();
                public static HealthNode CreateDelegate(string name, System.Func<HealthEvaluation> check) => new HealthNode();
                public static HealthNode CreateComposite(string name) => new HealthNode();
            }

            public enum Importance { Required, Important, Optional }
        }
        namespace Prognosis.DependencyInjection
        {
            public sealed class DependencyConfigurator
            {
                public DependencyConfigurator DependsOn(string serviceName, Prognosis.Importance importance) => this;
            }
        }
        """;

    [Fact]
    public async Task ValidReference_NoDiagnostic()
    {
        var source = Stubs + """

            class Setup
            {
                void Configure()
                {
                    var node = Prognosis.HealthNode.Create("Database");
                    var configurator = new Prognosis.DependencyInjection.DependencyConfigurator();
                    configurator.DependsOn("Database", Prognosis.Importance.Required);
                }
            }
            """;

        await RunAnalyzerTest(source);
    }

    [Fact]
    public async Task UnknownReference_ReportsDiagnostic()
    {
        var source = Stubs + """

            class Setup
            {
                void Configure()
                {
                    var node = Prognosis.HealthNode.Create("Database");
                    var configurator = new Prognosis.DependencyInjection.DependencyConfigurator();
                    configurator.DependsOn({|#0:"Databse"|}, Prognosis.Importance.Required);
                }
            }
            """;

        await RunAnalyzerTest(source,
            new DiagnosticResult(DependsOnEdgeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Databse"));
    }

    [Fact]
    public async Task MultipleReferences_OnlyUnknownFlagged()
    {
        var source = Stubs + """

            class Setup
            {
                void Configure()
                {
                    Prognosis.HealthNode.Create("Database");
                    Prognosis.HealthNode.Create("Cache");
                    var c = new Prognosis.DependencyInjection.DependencyConfigurator();
                    c.DependsOn("Database", Prognosis.Importance.Required);
                    c.DependsOn("Cache", Prognosis.Importance.Important);
                    c.DependsOn({|#0:"AuthService"|}, Prognosis.Importance.Required);
                }
            }
            """;

        await RunAnalyzerTest(source,
            new DiagnosticResult(DependsOnEdgeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("AuthService"));
    }

    [Fact]
    public async Task NoReferences_NoDiagnostic()
    {
        var source = Stubs + """

            class Setup
            {
                void Configure()
                {
                    Prognosis.HealthNode.Create("Database");
                }
            }
            """;

        await RunAnalyzerTest(source);
    }

    [Fact]
    public async Task ConstReference_Validated()
    {
        var source = Stubs + """

            class Setup
            {
                const string DbName = "Database";
                void Configure()
                {
                    Prognosis.HealthNode.Create(DbName);
                    var c = new Prognosis.DependencyInjection.DependencyConfigurator();
                    c.DependsOn(DbName, Prognosis.Importance.Required);
                }
            }
            """;

        await RunAnalyzerTest(source);
    }

    [Fact]
    public async Task ConstReference_Typo_ReportsDiagnostic()
    {
        var source = Stubs + """

            class Setup
            {
                const string BadName = "Databse";
                void Configure()
                {
                    Prognosis.HealthNode.Create("Database");
                    var c = new Prognosis.DependencyInjection.DependencyConfigurator();
                    c.DependsOn({|#0:BadName|}, Prognosis.Importance.Required);
                }
            }
            """;

        await RunAnalyzerTest(source,
            new DiagnosticResult(DependsOnEdgeAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithLocation(0)
                .WithArguments("Databse"));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task RunAnalyzerTest(
        string source,
        params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<DependsOnEdgeAnalyzer, DefaultVerifier>
        {
            TestState =
            {
                Sources = { source },
                ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
            },
            CompilerDiagnostics = CompilerDiagnostics.None,
        };

        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
