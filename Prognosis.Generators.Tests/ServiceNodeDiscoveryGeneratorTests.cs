using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Prognosis.Generators.Tests;

/// <summary>
/// Covers <see cref="ServiceNodeDiscoveryGenerator"/>'s LF output (the CRLF defect) and its
/// <c>[DependsOn]</c> → <c>Importance</c> mapping, which is positional and must stay in
/// lockstep with the enum's declaration order (ADR-008).
/// </summary>
public class ServiceNodeDiscoveryGeneratorTests
{
    /// <summary>
    /// Every declared <see cref="Importance"/> member must round-trip through the attribute and
    /// come out the other side as itself.
    /// </summary>
    [Theory]
    [InlineData(Importance.Required)]
    [InlineData(Importance.Important)]
    [InlineData(Importance.Optional)]
    [InlineData(Importance.Resilient)]
    [InlineData(Importance.Advisory)]
    public void DependsOnAttribute_GeneratesTheDeclaredImportance(Importance importance)
    {
        var generated = RunGenerator($$"""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep", Importance.{{importance}})]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains($"deps.DependsOn(\"Dep\", Importance.{importance});", generated);
    }

    /// <summary>
    /// The totality guard proper: no declared member may fall through to the default arm. Driven
    /// by <c>Enum.GetValues</c> rather than a fixed list, so <b>adding a member to the enum fails
    /// this test until the generator's positional map is updated</b> — the guard that was missing.
    /// If the enum gains a member and the map does not, the emitted text carries the deliberate
    /// <c>__UnmappedImportanceValue_*</c> sentinel instead of silently reading
    /// <c>Importance.Required</c>.
    /// </summary>
    [Fact]
    public void EveryDeclaredImportance_IsMapped_NoneFallsThroughToTheSentinel()
    {
        foreach (var importance in Enum.GetValues<Importance>())
        {
            var generated = RunGenerator($$"""
                using Prognosis;
                using Prognosis.DependencyInjection;

                public class Svc
                {
                    [DependsOn("Dep", Importance.{{importance}})]
                    public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
                }
                """);

            Assert.False(
                generated.Contains("__UnmappedImportanceValue_"),
                $"ServiceNodeDiscoveryGenerator has no arm for Importance.{importance}; it would "
                    + "emit an unmapped sentinel. Update the positional map. See ADR-008.");

            Assert.Contains($"Importance.{importance}", generated);
        }
    }

    /// <summary>
    /// An advisory edge must survive discovery as advisory. This is the end-to-end case the ADR
    /// cares about: attribute → generator → wiring call. Had the old silent fallback remained,
    /// this would have produced a fully-gating <c>Required</c> edge and the health graph would
    /// not have matched its own source.
    /// </summary>
    [Fact]
    public void AdvisoryEdge_IsNotSilentlyDowngradedToRequired()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Inference", Importance.Advisory)]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains("deps.DependsOn(\"Inference\", Importance.Advisory);", generated);
        Assert.DoesNotContain("Importance.Required", generated);
    }

    /// <summary>
    /// The fail-loud path itself. An <em>undeclared</em> importance — which is precisely what a
    /// future enum member looks like to a generator that has not been updated — must NOT be
    /// mapped to a plausible value. It emits an uncompilable sentinel so the consumer's build
    /// breaks visibly, instead of the old behaviour: a silent, fully-gating
    /// <c>Importance.Required</c> edge that the source never asked for.
    /// </summary>
    [Fact]
    public void UndeclaredImportance_EmitsUncompilableSentinel_NotASilentRequired()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep", (Importance)99)]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains("__UnmappedImportanceValue_99__SeeAdr008", generated);
        Assert.DoesNotContain("Importance.Required", generated);
    }

    /// <summary>
    /// Documents what a <em>malformed</em> importance argument actually does — which is not what
    /// it looks like from the source. When the argument fails to bind, Roslyn drops the attribute's
    /// <c>ConstructorArguments</c> entirely, so the generator never reaches its importance map: the
    /// dependency is <b>silently omitted</b> rather than mis-weighted. The node still registers,
    /// just with no edge.
    /// <para>
    /// Pinned so the behaviour is visible rather than folklore. It is still a silent failure of the
    /// kind ADR-008 targets, but it lives upstream of the importance mapping and fixing it means
    /// reporting a generator diagnostic — tracked separately, out of scope here.
    /// </para>
    /// </summary>
    [Fact]
    public void MalformedImportanceArgument_SilentlyDropsTheEdge_NotASilentRequired()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep", "not-an-importance")]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        // The node registers...
        Assert.Contains("AddServiceNode<global::Svc>(svc => svc.HealthNode);", generated);

        // ...but the edge is gone entirely — and crucially NOT silently downgraded to Required.
        Assert.DoesNotContain("DependsOn(", generated);
        Assert.DoesNotContain("Importance.Required", generated);
    }

    /// <summary>
    /// Omitting the argument entirely is NOT malformed — it selects the attribute's own default,
    /// so <c>Required</c> here is declared intent rather than a silent substitution.
    /// </summary>
    [Fact]
    public void OmittedImportanceArgument_UsesTheAttributeDefault()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep")]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains("deps.DependsOn(\"Dep\", Importance.Required);", generated);
        Assert.DoesNotContain("__Malformed", generated);
    }

    /// <summary>
    /// Generated source must use bare LF on every platform (the CRLF defect).
    /// <para>
    /// The sibling assertion in <see cref="HealthNodeNameCollectorTests"/> covers only the other
    /// generator, so without this the <c>AppendLineLf</c> call sites in this file would be
    /// unprotected — neither test can speak for the other.
    /// </para>
    /// </summary>
    [Fact]
    public void GeneratedSource_UsesLfLineEndings_OnEveryPlatform()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep", Importance.Important)]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains("\n", generated);      // guard: the sample really is multi-line
        Assert.DoesNotContain("\r", generated);
    }

    /// <summary>
    /// Smoke coverage for the discovery path itself, so the byte-level assertion above cannot
    /// pass against trivially-empty output.
    /// </summary>
    [Fact]
    public void DependsOnAttribute_EmitsWiringForTheDeclaredEdge()
    {
        var generated = RunGenerator("""
            using Prognosis;
            using Prognosis.DependencyInjection;

            public class Svc
            {
                [DependsOn("Dep", Importance.Important)]
                public HealthNode HealthNode { get; } = HealthNode.Create("Svc");
            }
            """);

        Assert.Contains("AddDiscoveredNodes", generated);
        Assert.Contains("deps.DependsOn(\"Dep\", Importance.Important);", generated);
    }

    private static string RunGenerator(string source) =>
        GeneratorTestHarness.Run<ServiceNodeDiscoveryGenerator>(source);
}
