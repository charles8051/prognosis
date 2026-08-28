using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Prognosis.Generators.Tests;

/// <summary>
/// Shared Roslyn setup for driving a generator over a source snippet. One copy, so the two
/// generator test classes cannot drift when the compilation setup needs to change.
/// </summary>
internal static class GeneratorTestHarness
{
    /// <summary>
    /// Runs <typeparamref name="TGenerator"/> over <paramref name="source"/> and returns the
    /// concatenated generated source.
    /// </summary>
    internal static string Run<TGenerator>(string source)
        where TGenerator : IIncrementalGenerator, new()
    {
        var driver = CSharpGeneratorDriver
            .Create(new TGenerator())
            .RunGenerators(Compile(source));

        var trees = driver.GetRunResult().GeneratedTrees;
        Assert.NotEmpty(trees);

        return string.Concat(trees.Select(t => t.GetText().ToString()));
    }

    /// <summary>
    /// Builds the test compilation: the snippet plus every loaded assembly, plus explicit
    /// Prognosis / Prognosis.DependencyInjection references (see the comment below — they look
    /// redundant and are not).
    /// </summary>
    internal static CSharpCompilation Compile(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        // Not redundant with the sweep: .NET loads assemblies lazily, and the typeof(...) here is
        // what forces the load. Removing these passes the full suite but fails a single test class
        // run in isolation (the generator emits nothing unless PrognosisBuilder resolves).
        references.Add(MetadataReference.CreateFromFile(typeof(Prognosis.HealthNode).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(
            typeof(Prognosis.DependencyInjection.PrognosisBuilder).Assembly.Location));

        return CSharpCompilation.Create(
            "TestCompilation",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
