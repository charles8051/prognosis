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
    /// Polls the full health graph on the given interval, calling
    /// <see cref="HealthNode.NotifyChanged"/> on every node before
    /// producing each <see cref="HealthReport"/>.
    /// Only emits when the report changes.
    /// Re-queries <see cref="HealthGraph.Roots"/> each tick so runtime
    /// edge changes are reflected automatically.
    /// </summary>
    public static IObservable<HealthReport> PollHealthReport(
        this HealthGraph graph,
        TimeSpan interval)
    {
        return Observable.Interval(interval)
            .Select(_ =>
            {
                var roots = graph.Roots;
                HealthAggregator.NotifyGraph(roots);
                return HealthAggregator.CreateReport(roots);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Polls the full health graph on the given interval, calling
    /// <see cref="HealthNode.NotifyChanged"/> on every node before
    /// producing each <see cref="HealthReport"/>.
    /// Only emits when the report changes.
    /// </summary>
    public static IObservable<HealthReport> PollHealthReport(
        this IReadOnlyList<HealthNode> roots,
        TimeSpan interval)
    {
        var rootsArray = roots as HealthNode[] ?? roots.ToArray();
        return Observable.Interval(interval)
            .Select(_ =>
            {
                HealthAggregator.NotifyGraph(rootsArray);
                return HealthAggregator.CreateReport(rootsArray);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Produces a new <see cref="HealthReport"/> whenever any service in
    /// the graph signals a change, throttled to avoid evaluation storms.
    /// Subscribes to all services (not just current leaves) so that runtime
    /// edge changes are handled. Re-queries <see cref="HealthGraph.Roots"/>
    /// when building each report.
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this HealthGraph graph,
        TimeSpan throttle)
    {
        return graph.Services
            .Select(n => n.StatusChanged)
            .Merge()
            .Throttle(throttle)
            .Select(_ =>
            {
                var roots = graph.Roots;
                HealthAggregator.NotifyGraph(roots);
                return HealthAggregator.CreateReport(roots);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Produces a new <see cref="HealthReport"/> whenever any leaf node in
    /// the graph signals a change, throttled to avoid evaluation storms.
    /// Combines push-based triggers with the single-pass evaluation of
    /// <see cref="HealthAggregator.CreateReport"/>.
    /// Only leaf nodes (those with no dependencies) are observed as triggers,
    /// since parent status changes are always a consequence of
    /// <see cref="HealthAggregator.NotifyGraph"/>, not exogenous events.
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this IReadOnlyList<HealthNode> roots,
        TimeSpan throttle)
    {
        var rootsArray = roots as HealthNode[] ?? roots.ToArray();
        return WalkNodes(rootsArray)
            .Where(n => n.Dependencies.Count == 0)
            .Select(n => n.StatusChanged)
            .Merge()
            .Throttle(throttle)
            .Select(_ =>
            {
                HealthAggregator.NotifyGraph(rootsArray);
                return HealthAggregator.CreateReport(rootsArray);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
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
            .SelectMany(state => HealthAggregator.Diff(state.Previous!, state.Current!));
    }

    private static IObservable<HealthNode> WalkNodes(HealthNode[] roots)
    {
        return Observable.Create<HealthNode>(observer =>
        {
            var visited = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<HealthNode>(roots);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                    continue;

                observer.OnNext(current);

                foreach (var dep in current.Dependencies)
                    stack.Push(dep.Service);
            }

            observer.OnCompleted();
            return System.Reactive.Disposables.Disposable.Empty;
        });
    }
}
