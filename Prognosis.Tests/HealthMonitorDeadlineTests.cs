using System.Diagnostics;

namespace Prognosis.Tests;

/// <summary>
/// Deadline-aware <see cref="HealthMonitor"/> behaviour, driven entirely off a
/// deterministic virtual clock: the SAME fake time source backs both the graph's clock
/// (so waves read fake wave time) and the monitor's wait primitive (an injected
/// <c>IMonitorDelay</c>), so every assertion is on accumulated FAKE time with no real
/// sleeps in the assertion path. The virtual clock's waits complete only when the test
/// advances it or the monitor re-arms — no wall-clock timers.
/// </summary>
public class HealthMonitorDeadlineTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    // Deterministic settle: block until a monitor-driven condition holds. The condition
    // is the signal; the timeout is only a hang guard, never the assertion mechanism.
    private static void WaitFor(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail($"timed out waiting for: {what}");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Monitor_WakesAtDeadline_NotCadence()
    {
        var clock = new VirtualClock();
        var probe = new Probe(HealthEvaluation.Healthy);
        var node = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(Sec(10)));
        var graph = HealthGraph.Create(node, clock.Read);

        using var monitor = new HealthMonitor(graph, cadence: Sec(1000), clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "initial arm");

        // Edge-driven observation: device drops at t=5, the tracker Refreshes the node.
        // That installs a debounce deadline at 5 + 10 = 15 and moves TemporalDeadline,
        // so the monitor re-arms from the cadence (1000) to 15.
        clock.AdvanceSeconds(5);
        probe.Set(HealthEvaluation.Unhealthy("gone"));
        var regs = clock.Registrations;
        node.Refresh();
        WaitFor(() => clock.Registrations > regs && clock.IsArmed, "re-arm to policy deadline 15");

        // Wake at the deadline (15), well before the cadence (1000).
        clock.AdvanceSeconds(10); // t = 15
        WaitFor(() => monitor.WavesForTest >= 1, "deadline wave at 15");

        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
    }

    [Fact]
    public void Monitor_ReArmsWhenDeadlineMovesLater_AndDoesNotFireAtTheStaleInstant()
    {
        var clock = new VirtualClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);         // decay at 90

        using var monitor = new HealthMonitor(graph, cadence: null, clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "armed at 90");

        // Re-affirm at t=10 → decay moves 90 → 100 (later). Monitor must re-arm.
        clock.AdvanceSeconds(10);
        var regs = clock.Registrations;
        lease.Affirm(HealthEvaluation.Healthy);
        WaitFor(() => clock.Registrations > regs && clock.IsArmed, "re-arm to 100");

        // Cross the STALE deadline (90): the monitor must NOT wave there.
        clock.AdvanceSeconds(80); // t = 90
        Assert.Equal(0, monitor.WavesForTest);

        // Cross the real deadline (100 + jitter): decays to Unknown. (Lease freshness is
        // age <= ttl inclusive, so we advance just past, modelling real timer jitter.)
        clock.AdvanceSeconds(11); // t = 101, age = 91 > 90
        WaitFor(() => monitor.WavesForTest >= 1, "decay wave past 100");
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);
    }

    [Fact]
    public void Monitor_DoesNotWaveBeforeAFarDeadline()
    {
        var clock = new VirtualClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(1000), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);         // decay at 1000

        using var monitor = new HealthMonitor(graph, cadence: null, clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "armed at 1000");

        // Advance well short of the deadline: the monitor sleeps on the single pending
        // wait (it cannot busy-loop — the virtual wait only completes at the instant or
        // on a re-arm), so no wave fires.
        clock.AdvanceSeconds(500);
        Assert.Equal(0, monitor.WavesForTest);
        Assert.Equal(1, clock.PendingCompletions); // exactly one wait registered, still pending
    }

    [Fact]
    public void Monitor_CadenceOptional_DeadlineOnly_FiresOnlyOnDeadlines()
    {
        var clock = new VirtualClock();
        var node = HealthNode.Create("Leased");
        var lease = node.Lease(new HealthLeaseOptions(Sec(90), EscalateAfter: Sec(90), Clock: clock.Read));
        var graph = HealthGraph.Create(node, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);

        using var monitor = new HealthMonitor(graph, cadence: null, clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "armed at 90");

        // Stage one: past ttl (90) → Unknown.
        clock.AdvanceSeconds(91); // t = 91
        WaitFor(() => monitor.WavesForTest >= 1 && clock.IsArmed, "expiry wave, re-armed to escalation");
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Root.Status);
        var wavesAfterExpiry = monitor.WavesForTest;

        // Stage two: past ttl + escalateAfter (180) → escalate. No spurious waves between.
        clock.AdvanceSeconds(90); // t = 181
        WaitFor(() => monitor.WavesForTest > wavesAfterExpiry, "escalation wave");
        Assert.Equal(HealthStatus.Degraded, graph.GetReport().Root.Status);

        // Fully escalated: stable verdict, no further deadline, no cadence → parks.
        Assert.Null(graph.NextTemporalDeadline);
    }

    [Fact]
    public void Monitor_DriftingPullProbe_IsPolledOnCadence()
    {
        var clock = new VirtualClock();
        var flag = new Probe(HealthEvaluation.Healthy);
        // A plain pull-probe: no policy, no lease → no computable deadline. Only a cadence
        // poll can observe its drift.
        var node = HealthNode.Create("Probe").WithHealthProbe(flag.Read);
        var graph = HealthGraph.Create(node, clock.Read);

        using var monitor = new HealthMonitor(graph, cadence: Sec(50), clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "armed on cadence");

        // The flag drifts with no edge and no deadline; unobserved until the cadence tick.
        flag.Set(HealthEvaluation.Unhealthy("drift"));
        Assert.Equal(HealthStatus.Healthy, graph.GetReport().Root.Status);

        clock.AdvanceSeconds(50); // cadence tick
        WaitFor(() => monitor.WavesForTest >= 1, "cadence poll");
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Root.Status);
    }

    [Fact]
    public void Monitor_SingleMin_ServicesBothLeaseAndPolicy()
    {
        var clock = new VirtualClock();
        var leaseNode = HealthNode.Create("Lease");
        var lease = leaseNode.Lease(new HealthLeaseOptions(Sec(90), Clock: clock.Read));

        var probe = new Probe(HealthEvaluation.Healthy);
        var peripheralNode = HealthNode.Create("Peripheral").WithHealthProbe(probe.Read)
            .WithDebounce(new DebounceOptions(Sec(10)));

        var root = HealthNode.Create("Root")
            .DependsOn(leaseNode, Importance.Required)
            .DependsOn(peripheralNode, Importance.Required);
        var graph = HealthGraph.Create(root, clock.Read);
        lease.Affirm(HealthEvaluation.Healthy);         // lease decay at 90

        using var monitor = new HealthMonitor(graph, cadence: null, clock);
        monitor.Start();
        WaitFor(() => clock.IsArmed, "armed at lease deadline 90");

        // Peripheral drops at t=5: policy deadline 15 becomes the min. Monitor wakes at 15 and
        // the policy gates — serviced by the single min.
        clock.AdvanceSeconds(5);
        probe.Set(HealthEvaluation.Unhealthy("gone"));
        var regs = clock.Registrations;
        peripheralNode.Refresh();
        WaitFor(() => clock.Registrations > regs && clock.IsArmed, "re-arm to policy min 15");

        clock.AdvanceSeconds(10); // t = 15
        WaitFor(() => monitor.WavesForTest >= 1, "policy gate wave at 15");
        Assert.Equal(HealthStatus.Unhealthy, graph.GetReport().Nodes.First(n => n.Name == "Peripheral").Status);
        var wavesAfterPolicy = monitor.WavesForTest;

        // Now the lease (90) is the min. Monitor wakes past it and the lease decays —
        // the SAME monitor, the SAME single min, servicing the other temporal kind.
        clock.AdvanceSeconds(76); // t = 91 > 90
        WaitFor(() => monitor.WavesForTest > wavesAfterPolicy, "lease decay wave past 90");
        Assert.Equal(HealthStatus.Unknown, graph.GetReport().Nodes.First(n => n.Name == "Lease").Status);
    }

    private sealed class Probe(HealthEvaluation initial)
    {
        private HealthEvaluation _value = initial;
        public HealthEvaluation Read() => Volatile.Read(ref _value);
        public void Set(HealthEvaluation value) => Volatile.Write(ref _value, value);
    }

    /// <summary>
    /// A virtual time source that backs BOTH the graph clock (<see cref="Read"/>, a
    /// <c>Func&lt;long&gt;</c>) and the monitor's wait primitive
    /// (<see cref="IMonitorDelay"/>). A wait completes only when the test advances past
    /// its instant or the monitor re-arms — no real timers, so the monitor's timing is
    /// deterministic against accumulated fake time. Bounded like the production
    /// <c>SemaphoreSlim(0, 1)</c>: a <see cref="Wake"/> with no waiter latches one permit.
    /// </summary>
    private sealed class VirtualClock : IMonitorDelay
    {
        private readonly object _gate = new();
        private long _ticks;
        private TaskCompletionSource<bool>? _pending;
        private TimeSpan? _pendingWakeAt;
        private bool _wakeLatched;
        private int _registrations;

        public long Read() => Interlocked.Read(ref _ticks);
        private TimeSpan Now => TimeSpan.FromSeconds(Interlocked.Read(ref _ticks) / (double)Stopwatch.Frequency);

        public int Registrations => Volatile.Read(ref _registrations);
        public bool IsArmed { get { lock (_gate) return _pending is not null; } }
        public int PendingCompletions { get { lock (_gate) return _pending is not null ? 1 : 0; } }

        public void AdvanceSeconds(double seconds)
        {
            var add = (long)(seconds * Stopwatch.Frequency);
            lock (_gate)
            {
                _ticks += add;
                if (_pending is { } tcs && _pendingWakeAt is TimeSpan w && Now >= w)
                {
                    _pending = null;
                    _pendingWakeAt = null;
                    tcs.TrySetResult(false); // the instant was reached
                }
            }
        }

        public Task<bool> WaitUntilAsync(TimeSpan? wakeAt, CancellationToken ct)
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _registrations);

                if (_wakeLatched)
                {
                    _wakeLatched = false;
                    return Task.FromResult(true); // consume a latched re-arm
                }
                if (wakeAt is TimeSpan w && Now >= w)
                    return Task.FromResult(false); // already due

                // RunContinuationsAsynchronously: the monitor loop must NOT resume inline
                // on the test's Advance/Wake thread (it would re-enter this lock).
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = tcs;
                _pendingWakeAt = wakeAt;
                if (ct.CanBeCanceled)
                    ct.Register(() =>
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_pending, tcs)) { _pending = null; _pendingWakeAt = null; }
                        }
                        tcs.TrySetCanceled();
                    });
                return tcs.Task;
            }
        }

        public void Wake()
        {
            lock (_gate)
            {
                if (_pending is { } tcs)
                {
                    _pending = null;
                    _pendingWakeAt = null;
                    tcs.TrySetResult(true);
                }
                else
                {
                    _wakeLatched = true;
                }
            }
        }
    }
}
