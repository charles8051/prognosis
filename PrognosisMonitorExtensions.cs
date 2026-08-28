namespace Prognosis;

/// <summary>
/// Core one-liner for standing up a <see cref="HealthMonitor"/> — the blessed wave
/// source (ADR-010 §6 / ADR-011 §6). For Rx and manual consumers who are not using the
/// <c>Prognosis.DependencyInjection</c> hosted-service path; DI users get the equivalent
/// via <c>PrognosisBuilder.UseMonitor</c>.
/// </summary>
public static class PrognosisMonitorExtensions
{
    /// <summary>
    /// Creates a <see cref="HealthMonitor"/> for this graph, starts it, and returns it
    /// (dispose to stop). Turns "temporal features require a wave source" into a single
    /// call instead of a hand-rolled deadline pump: the monitor wakes on the graph's
    /// <see cref="HealthGraph.NextTemporalDeadline"/> (policies AND leases) and, if a
    /// cadence is given, also polls at least that often for drifting pull-probes.
    /// </summary>
    /// <param name="graph">The graph to drive.</param>
    /// <param name="cadence">
    /// Optional fixed poll interval. Omit for a purely deadline-driven graph; supply it
    /// when a temporal node's input is a drifting pull-probe whose change has no
    /// computable deadline. Must be positive when supplied.
    /// </param>
    /// <returns>The started monitor. Dispose it to stop driving the graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cadence"/> is non-positive.</exception>
    public static HealthMonitor RunMonitor(this HealthGraph graph, TimeSpan? cadence = null)
    {
        _ = graph ?? throw new ArgumentNullException(nameof(graph));
        var monitor = new HealthMonitor(graph, cadence);
        monitor.Start();
        return monitor;
    }
}
