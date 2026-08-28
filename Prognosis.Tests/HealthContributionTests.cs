using Prognosis;

namespace Prognosis.Tests;

/// <summary>
/// Pins <see cref="HealthContribution.Of"/> — the single fold mapping shared by the
/// live rollup (<c>HealthNode.Aggregate</c>) and the diagnostic re-fold (ADR-007).
/// Mirrors the ADR-006 <c>Importance × status</c> table discipline so a change to the
/// mapping cannot pass silently, and cross-checks the helper against real aggregation.
/// </summary>
public class HealthContributionTests
{
    [Theory]
    // Required — status passes through unchanged.
    [InlineData(Importance.Required, HealthStatus.Healthy, false, HealthStatus.Healthy)]
    [InlineData(Importance.Required, HealthStatus.Degraded, false, HealthStatus.Degraded)]
    [InlineData(Importance.Required, HealthStatus.Unhealthy, false, HealthStatus.Unhealthy)]
    // Important — Unhealthy caps to Degraded; else passes through.
    [InlineData(Importance.Important, HealthStatus.Healthy, false, HealthStatus.Healthy)]
    [InlineData(Importance.Important, HealthStatus.Degraded, false, HealthStatus.Degraded)]
    [InlineData(Importance.Important, HealthStatus.Unhealthy, false, HealthStatus.Degraded)]
    // Optional — always Healthy (dependency ignored).
    [InlineData(Importance.Optional, HealthStatus.Healthy, false, HealthStatus.Healthy)]
    [InlineData(Importance.Optional, HealthStatus.Degraded, false, HealthStatus.Healthy)]
    [InlineData(Importance.Optional, HealthStatus.Unhealthy, false, HealthStatus.Healthy)]
    // Resilient — Unhealthy caps to Degraded iff a healthy resilient sibling exists (quorum).
    [InlineData(Importance.Resilient, HealthStatus.Unhealthy, false, HealthStatus.Unhealthy)]
    [InlineData(Importance.Resilient, HealthStatus.Unhealthy, true, HealthStatus.Degraded)]
    [InlineData(Importance.Resilient, HealthStatus.Degraded, false, HealthStatus.Degraded)]
    [InlineData(Importance.Resilient, HealthStatus.Degraded, true, HealthStatus.Degraded)]
    [InlineData(Importance.Resilient, HealthStatus.Healthy, false, HealthStatus.Healthy)]
    // ADR-006: Unknown is strictly non-gating. For every gating importance the helper
    // passes Unknown through unchanged, and Unknown ranks below Degraded/Unhealthy, so
    // an Unknown child can never contribute a failing state. Optional ignores it entirely.
    [InlineData(Importance.Required, HealthStatus.Unknown, false, HealthStatus.Unknown)]
    [InlineData(Importance.Important, HealthStatus.Unknown, false, HealthStatus.Unknown)]
    [InlineData(Importance.Optional, HealthStatus.Unknown, false, HealthStatus.Healthy)]
    [InlineData(Importance.Resilient, HealthStatus.Unknown, false, HealthStatus.Unknown)]
    [InlineData(Importance.Resilient, HealthStatus.Unknown, true, HealthStatus.Unknown)]
    // ADR-008: Advisory == Important, EXCEPT that Unknown is absorbed to Healthy. The first
    // row is the whole point of the member; the rest pin that it is otherwise Important.
    [InlineData(Importance.Advisory, HealthStatus.Unknown, false, HealthStatus.Healthy)]
    [InlineData(Importance.Advisory, HealthStatus.Healthy, false, HealthStatus.Healthy)]
    [InlineData(Importance.Advisory, HealthStatus.Degraded, false, HealthStatus.Degraded)]
    [InlineData(Importance.Advisory, HealthStatus.Unhealthy, false, HealthStatus.Degraded)]
    public void Of_MapsContributionByImportance(
        Importance importance,
        HealthStatus childStatus,
        bool hasHealthyResilientSibling,
        HealthStatus expected)
    {
        Assert.Equal(
            expected,
            HealthContribution.Of(importance, childStatus, hasHealthyResilientSibling));
    }

    /// <summary>
    /// Anti-drift: a single-dependency parent aggregated by the live graph must land on
    /// exactly the status <see cref="HealthContribution.Of"/> predicts, for every
    /// (importance, child-status) pair. This is what makes the diagnostic re-fold and
    /// production aggregation one source of truth.
    /// </summary>
    [Theory]
    [InlineData(Importance.Required, HealthStatus.Unhealthy)]
    [InlineData(Importance.Required, HealthStatus.Degraded)]
    [InlineData(Importance.Required, HealthStatus.Unknown)]
    [InlineData(Importance.Required, HealthStatus.Healthy)]
    [InlineData(Importance.Important, HealthStatus.Unhealthy)]
    [InlineData(Importance.Important, HealthStatus.Degraded)]
    [InlineData(Importance.Important, HealthStatus.Unknown)]
    [InlineData(Importance.Optional, HealthStatus.Unhealthy)]
    [InlineData(Importance.Optional, HealthStatus.Unknown)]
    [InlineData(Importance.Advisory, HealthStatus.Unhealthy)]
    [InlineData(Importance.Advisory, HealthStatus.Degraded)]
    [InlineData(Importance.Advisory, HealthStatus.Unknown)]
    [InlineData(Importance.Advisory, HealthStatus.Healthy)]
    public void Of_AgreesWithLiveAggregate_SingleDependency(
        Importance importance, HealthStatus childStatus)
    {
        var dep = HealthNode.Create("Dep").WithHealthProbe(() => childStatus);
        var parent = HealthNode.Create("Parent").DependsOn(dep, importance);
        var graph = HealthGraph.Create(parent);

        // No resilient sibling in this single-dep shape, so quorum is false.
        var expected = HealthContribution.Of(importance, childStatus, hasHealthyResilientSibling: false);

        Assert.Equal(expected, graph.GetReport().Nodes.First(n => n.Name == "Parent").Status);
    }

    /// <summary>
    /// Totality guard (ADR-008 §4). Every declared <see cref="Importance"/> member must be
    /// handled explicitly — no silent default. Written as a reflection sweep rather than a
    /// fixed list so that <b>adding a member to the enum fails this test until the fold is
    /// updated</b>. That is the whole mechanism: the previous <c>_ =&gt; Healthy</c> default
    /// meant a new member silently contributed nothing, and nothing anywhere said so.
    /// </summary>
    [Fact]
    public void Of_HandlesEveryDeclaredImportance()
    {
        foreach (var importance in Enum.GetValues<Importance>())
        {
            foreach (var status in Enum.GetValues<HealthStatus>())
            {
                var ex = Record.Exception(() => HealthContribution.Of(importance, status, false));
                Assert.True(
                    ex is null,
                    $"HealthContribution.Of({importance}, {status}) threw — the fold is missing "
                        + $"an arm for {importance}. See ADR-008.");
            }
        }
    }

    /// <summary>
    /// The other half of the guard: an <em>undeclared</em> value must throw rather than be
    /// silently mapped to a plausible status. Casting an out-of-range int is the only way to
    /// reach the default arm, and is exactly what a future enum member looks like to a stale
    /// build of a downstream consumer.
    /// </summary>
    [Fact]
    public void Of_ThrowsOnUndeclaredImportance_RatherThanGuessing()
    {
        var undeclared = (Importance)999;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HealthContribution.Of(undeclared, HealthStatus.Unhealthy, false));
    }
}
