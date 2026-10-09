using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace Prognosis.Generators.Tests;

/// <summary>
/// References for test compilations that also reference the Prognosis assemblies loaded in this
/// test process.
/// </summary>
/// <remarks>
/// This project targets net10.0, so it loads the net10.0 build of Prognosis, which references
/// System.Runtime 10.0. A compilation that adds that assembly against older reference assemblies
/// (for example <c>ReferenceAssemblies.Net.Net90</c>) fails with CS1705. Compiling against the
/// running shared framework keeps the two in step whatever runtime the tests use, and needs no
/// reference-pack download. <see cref="GeneratorTestHarness"/> compiles the same way.
/// </remarks>
internal static class TestReferenceAssemblies
{
    /// <summary>No reference package; pair it with <see cref="SharedFramework"/>.</summary>
    public static ReferenceAssemblies None { get; } = new("net10.0");

    /// <summary>Every assembly of the shared framework the tests are running on.</summary>
    public static ImmutableArray<MetadataReference> SharedFramework { get; } = LoadSharedFramework();

    private static ImmutableArray<MetadataReference> LoadSharedFramework()
    {
        var frameworkDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.StartsWith(frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}
