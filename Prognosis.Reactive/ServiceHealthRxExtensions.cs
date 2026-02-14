using System.Reactive.Linq;

namespace Prognosis.Reactive;

/// <summary>
/// System.Reactive extension methods for the Prognosis health graph.
/// These provide idiomatic Rx alternatives to the polling-based
/// <see cref="HealthMonitor"/> in the core package.
/// </summary>
public static class ServiceHealthRxExtensions
{
    /// <summary>
    /// Polls the full health graph on the given interval, calling
    /// <see cref="IObservableServiceHealth.NotifyChanged"/> on every observable
    /// service before producing each <see cref="HealthReport"/>.
    /// Only emits when the report changes.
    /// </summary>
    public static IObservable<HealthReport> PollHealthReport(
        this IServiceHealth[] roots,
        TimeSpan interval)
    {
        return Observable.Interval(interval)
            .Select(_ =>
            {
                HealthAggregator.NotifyGraph(roots);
                return HealthAggregator.CreateReport(roots);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Produces a new <see cref="HealthReport"/> whenever any observable leaf
    /// node in the graph signals a change, throttled to avoid evaluation storms.
    /// Combines push-based triggers with the single-pass evaluation of
    /// <see cref="HealthAggregator.CreateReport"/>.
    /// Only leaf nodes (those with no dependencies) are observed as triggers,
    /// since parent status changes are always a consequence of
    /// <see cref="HealthAggregator.NotifyGraph"/>, not exogenous events.
    /// </summary>
    public static IObservable<HealthReport> ObserveHealthReport(
        this IServiceHealth[] roots,
        TimeSpan throttle)
    {
        return WalkObservables(roots)
            .Where(s => s.Dependencies.Count == 0)
            .Select(s => s.StatusChanged)
            .Merge()
            .Throttle(throttle)
            .Select(_ =>
            {
                HealthAggregator.NotifyGraph(roots);
                return HealthAggregator.CreateReport(roots);
            })
            .DistinctUntilChanged(HealthReportComparer.Instance);
    }

    /// <summary>
    /// Projects a stream of <see cref="HealthReport"/>s into individual
    /// <see cref="ServiceStatusChange"/> events by diffing consecutive reports.
    /// Only services whose status actually changed are emitted.
    /// Composable with any report source — <see cref="PollHealthReport"/>,
    /// <see cref="ObserveHealthReport"/>, or custom pipelines.
    /// </summary>
    public static IObservable<ServiceStatusChange> SelectServiceChanges(
        this IObservable<HealthReport> reports)
    {
        return reports
            .Scan(
                (Previous: (HealthReport?)null, Current: (HealthReport?)null),
                (state, report) => (state.Current, report))
            .Where(state => state.Previous is not null)
            .SelectMany(state => HealthAggregator.Diff(state.Previous!, state.Current!));
    }

    private static IObservable<IObservableServiceHealth> WalkObservables(IServiceHealth[] roots)
    {
        return Observable.Create<IObservableServiceHealth>(observer =>
        {
            var visited = new HashSet<IServiceHealth>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<IServiceHealth>(roots);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                    continue;

                if (current is IObservableServiceHealth observable)
                    observer.OnNext(observable);

                foreach (var dep in current.Dependencies)
                    stack.Push(dep.Service);
            }

            observer.OnCompleted();
            return System.Reactive.Disposables.Disposable.Empty;
        });
    }
}
