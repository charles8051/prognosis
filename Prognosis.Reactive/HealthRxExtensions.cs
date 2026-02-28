using System.Reactive.Linq;

namespace Prognosis.Reactive;

/// <summary>
/// System.Reactive extension methods for the Prognosis health graph.
/// These provide idiomatic Rx alternatives to the polling-based
/// <see cref="HealthMonitor"/> in the core package.
/// </summary>
public static class HealthRxExtensions
{
    /// <summary>
    /// Projects a stream of <see cref="HealthReport"/>s into individual
    /// <see cref="StatusChange"/> events by diffing consecutive reports.
    /// Only nodes whose status actually changed are emitted.
    /// Composable with any report source — <see cref="PollHealthReport"/>,
    /// <see cref="ObserveHealthReport"/>, or custom pipelines.
    /// </summary>
    public static IObservable<StatusChange> SelectHealthChanges(
        this IObservable<HealthReport> reports)
    {
        return reports
            .Scan(
                (Previous: (HealthReport?)null, Current: (HealthReport?)null),
                (state, report) => (state.Current, report))
            .Where(state => state.Previous is not null)
            .SelectMany(state => state.Previous!.DiffTo(state.Current!));
    }

    /// <summary>
    /// Filters a <see cref="StatusChange"/> stream to only include changes
    /// for the specified node names. Case-sensitive ordinal comparison.
    /// </summary>
    /// <example>
    /// <code>
    /// graph.StatusChanged
    ///     .SelectHealthChanges()
    ///     .ForNodes("Database", "Cache")
    ///     .Subscribe(change => Console.WriteLine($"{change.Name}: {change.Current}"));
    /// </code>
    /// </example>
    public static IObservable<StatusChange> ForNodes(
        this IObservable<StatusChange> changes,
        params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        return changes.Where(c => set.Contains(c.Name));
    }

    // ── HealthGraph extensions ───────────────────────────────────────

    /// <summary>
    /// Polls the entire <see cref="HealthGraph"/> on the given interval,
    /// calling <see cref="HealthGraph.RefreshAll"/> to re-evaluate every
    /// node before producing each <see cref="HealthReport"/>.
    /// Only emits when the report changes.
    /// </summary>
    public static IObservable<HealthReport> PollHealthReport(
        this HealthGraph graph,
        TimeSpan interval)
    {
        return Observable.Interval(interval)
            .Select(_ =>
            {
                graph.RefreshAll();
                return graph.CreateReport();
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Produces a new <see cref="HealthReport"/> for the entire graph each
    /// time any node's effective health changes.
    /// <para>
    /// Changes in any transitive dependency surface via the graph's
    /// <see cref="HealthGraph.StatusChanged"/>. Only emitted when the
    /// report differs from the previous one.
    /// </para>
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this HealthGraph graph)
    {
        return Observable.Defer(() =>
            graph.StatusChanged
                .DistinctUntilChanged(HealthReportComparer.Instance));
    }
}
