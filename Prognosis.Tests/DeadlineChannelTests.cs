namespace Prognosis.Tests;

/// <summary>
/// The <see cref="HealthGraph.TemporalDeadlineChanged"/> channel's Rx grammar under
/// concurrency (ADR-011 §6a). The replay-on-subscribe delivery must never land an
/// <c>OnNext</c> after <c>OnCompleted</c> when a concurrent <see cref="HealthGraph.Dispose"/>
/// (which completes the channel) interleaves.
/// </summary>
public class DeadlineChannelTests
{
    /// <summary>
    /// Subscribe (which replays the current minimum) racing Dispose (which completes the
    /// channel) must uphold the terminal grammar: no <c>OnNext</c> after <c>OnCompleted</c>.
    /// The per-subscriber serializer + done-flag makes it impossible; the pre-fix lock-free
    /// re-check between the liveness test and the replay <c>OnNext</c> could let a
    /// completed observer still receive a replayed value. Falsification: revert to the
    /// lock-free re-check and this catches the ordering violation over many races.
    /// </summary>
    [Fact]
    public void TemporalDeadlineChanged_SubscribeRacingDispose_NeverDeliversOnNextAfterOnCompleted()
    {
        var violations = 0;
        const int iters = 4000;

        for (var i = 0; i < iters; i++)
        {
            // A fresh graph each iteration; construction seeds a value so Subscribe replays.
            var graph = HealthGraph.Create(HealthNode.Create("N"));
            var observer = new GrammarObserver();
            var start = new ManualResetEventSlim(false);

            var subscriber = new Thread(() =>
            {
                start.Wait();
                try { graph.TemporalDeadlineChanged.Subscribe(observer); }
                catch { /* subscribe never throws here; guard defensively */ }
            });
            var disposer = new Thread(() =>
            {
                start.Wait();
                graph.Dispose();
            });

            subscriber.Start();
            disposer.Start();
            start.Set();
            subscriber.Join();
            disposer.Join();

            if (observer.Violated)
                violations++;
        }

        Assert.Equal(0, violations);
    }

    private sealed class GrammarObserver : IObserver<TimeSpan?>
    {
        private int _completed;
        public bool Violated;

        public void OnNext(TimeSpan? value)
        {
            if (Volatile.Read(ref _completed) == 1)
                Violated = true;
        }

        public void OnError(Exception error) { }
        public void OnCompleted() => Volatile.Write(ref _completed, 1);
    }
}
