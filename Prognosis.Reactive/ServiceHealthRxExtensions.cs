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
    /// Merges <see cref="IObservableServiceHealth.StatusChanged"/> streams
    /// from every observable node reachable in the dependency graph into a
    /// single sequence of <c>(Name, Status)</c> tuples.
    /// </summary>
    public static IObservable<(string Name, HealthStatus Status)> MergeStatusChanges(
        this IServiceHealth[] roots)
    {
        return WalkObservables(roots)
            .Select(s => s.StatusChanged.Select(status => (s.Name, status)))
            .Merge();
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
