using System.Reactive.Linq;

namespace Prognosis.Reactive;

/// <summary>
/// Small helpers to create shared (multicasted) report streams from the
/// cold Rx providers in this package. These are convenience wrappers around
/// the standard Rx multicast operators and live in the Reactive package so
/// they are opt-in.
/// </summary>
public enum ShareStrategy
{
    /// <summary>
    /// Start when the first subscriber arrives and stop when the last unsubscribes.
    /// Uses <c>Publish().RefCount()</c>.
    /// </summary>
    RefCount,

    /// <summary>
    /// Like <see cref="RefCount"/> but replay the latest value to new subscribers.
    /// Uses <c>Replay(1).RefCount()</c>.
    /// </summary>
    ReplayLatest
}

public static class HealthRxShared
{
    public static IObservable<HealthReport> CreateSharedReportStream(
        this HealthGraph graph,
        TimeSpan interval,
        ShareStrategy strategy = ShareStrategy.RefCount)
    {
        var source = graph.PollHealthReport(interval);
        return strategy switch
        {
            ShareStrategy.ReplayLatest => source.Replay(1).RefCount(),
            _ => source.Publish().RefCount(),
        };
    }

    public static IObservable<HealthReport> CreateSharedObserveStream(
        this HealthGraph graph,
        ShareStrategy strategy = ShareStrategy.RefCount)
    {
        var source = graph.ObserveHealthReport();
        return strategy switch
        {
            ShareStrategy.ReplayLatest => source.Replay(1).RefCount(),
            _ => source.Publish().RefCount(),
        };
    }
}
