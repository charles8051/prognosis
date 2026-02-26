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
    /// Polls the node and its full dependency subtree on the given interval,
    /// calling <see cref="HealthNode.RefreshDescendants"/> to re-evaluate every
    /// intrinsic check before producing each <see cref="HealthReport"/>.
    /// Only emits when the report changes.
    /// </summary>
    public static IObservable<HealthReport> PollHealthReport(
        this HealthNode node,
        TimeSpan interval)
    {
        return Observable.Interval(interval)
            .Select(_ =>
            {
                node.RefreshDescendants();
                return node.CreateReport();
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Emits the node's <see cref="HealthEvaluation"/> (status and reason) each
    /// time the effective health changes. Because
    /// <see cref="HealthNode.BubbleChange"/> propagates upward, this reflects
    /// changes in the node's own intrinsic check as well as changes in any
    /// transitive dependency.
    /// </summary>
    public static IObservable<HealthEvaluation> ObserveStatus(
        this HealthNode node)
    {
        return Observable.Defer(() =>
            node.StatusChanged
                .Select(_ => node.Evaluate()));
    }

    /// <summary>
    /// Produces a new <see cref="HealthReport"/> for the node and its full
    /// dependency subtree each time the node's effective health changes.
    /// <para>
    /// Because <see cref="HealthNode.BubbleChange"/> propagates upward,
    /// subscribing to a single node captures changes from any transitive
    /// dependency. The report is built via <see cref="HealthNode.CreateReport"/>
    /// and only emitted when it differs from the previous one.
    /// </para>
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this HealthNode node)
    {
        return Observable.Defer(() =>
            node.StatusChanged
                .Select(_ => node.CreateReport())
                .DistinctUntilChanged(HealthReportComparer.Instance));
    }

    /// <summary>
    /// Projects a stream of <see cref="HealthReport"/>s into individual
    /// <see cref="StatusChange"/> events by diffing consecutive reports.
    /// Only services whose status actually changed are emitted.
    /// Composable with any report source — <see cref="PollHealthReport"/>,
    /// <see cref="ObserveHealthReport"/>, or custom pipelines.
    /// </summary>
    public static IObservable<StatusChange> SelectServiceChanges(
        this IObservable<HealthReport> reports)
    {
        return reports
            .Scan(
                (Previous: (HealthReport?)null, Current: (HealthReport?)null),
                (state, report) => (state.Current, report))
            .Where(state => state.Previous is not null)
            .SelectMany(state => state.Previous!.DiffTo(state.Current!));
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
    /// time the root node's effective health changes.
    /// <para>
    /// Because <see cref="HealthNode.BubbleChange"/> propagates upward, a change
    /// in any transitive dependency surfaces at the root and triggers a
    /// fresh report. Only emitted when the report differs from the previous one.
    /// </para>
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this HealthGraph graph)
    {
        return Observable.Defer(() =>
            graph.Root.StatusChanged
                .Select(_ => graph.CreateReport())
                .DistinctUntilChanged(HealthReportComparer.Instance));
    }
}
