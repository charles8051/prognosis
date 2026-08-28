namespace Prognosis;

/// <summary>
/// The per-node temporal observation record (ADR-011 §4). Immutable; the owning
/// <see cref="HealthNode"/> holds it inside a single atomically-swapped
/// <c>(Effective, History)</c> pair so a reader can never observe a fresh
/// evaluation against a stale history.
/// <para>
/// This records the node's <em>raw</em> observation trail — the pre-policy status
/// history — so a flap projection (<see cref="FlapWindow.Count"/>) reads the true
/// transition record even when a suppressing policy hides those transitions from
/// the effective status.
/// </para>
/// </summary>
/// <param name="LastRaw">
/// The most recently observed raw (post-<c>Aggregate</c>, pre-policy) status.
/// </param>
/// <param name="CurrentRunStartedAt">
/// The wave time at which <see cref="LastRaw"/> last changed — the start of the
/// current same-status run. In the graph's monotonic timebase (ADR-011 §5).
/// </param>
/// <param name="HasEverBeenLive">
/// The one-way grace latch (ADR-011 §3): set once the consumer reports the node
/// live via <see cref="HealthNode.MarkLive"/>, never cleared. The library never
/// infers this from verdicts.
/// </param>
/// <param name="PendingDeadline">
/// The earliest future instant at which a configured policy's answer could change
/// with no new observation (a debounce window's end, a grace deadline), or
/// <see langword="null"/> when nothing is pending. The graph exposes the minimum
/// over its nodes as <see cref="HealthGraph.NextTemporalDeadline"/> (ADR-011 §6).
/// </param>
/// <param name="Transitions">
/// The raw status-transition instants, bounded and drop-oldest at
/// <see cref="TransitionBound"/> (ADR-011 §4). Oldest-first.
/// </param>
public sealed record NodeObservationHistory(
    HealthStatus LastRaw,
    TimeSpan CurrentRunStartedAt,
    bool HasEverBeenLive,
    TimeSpan? PendingDeadline,
    IReadOnlyList<TimeSpan> Transitions)
{
    /// <summary>
    /// The fixed, library-owned bound on <see cref="Transitions"/> (ADR-011 §4):
    /// drop-oldest past this many recorded raw transitions. Fixed rather than
    /// configurable so replay is deterministic across implementations; a node
    /// flapping fast enough to saturate a 32-deep window is itself the signal.
    /// </summary>
    public const int TransitionBound = 32;

    private static readonly IReadOnlyList<TimeSpan> NoTransitions = Array.Empty<TimeSpan>();

    /// <summary>
    /// The seed history for a node that has never been evaluated in a wave: the
    /// given raw status, no timebase yet, not live, nothing pending, no
    /// transitions. <see cref="CurrentRunStartedAt"/> is stamped on the first wave
    /// (ADR-011 §5).
    /// </summary>
    internal static NodeObservationHistory Seed(HealthStatus seededRaw) =>
        new(seededRaw, TimeSpan.Zero, HasEverBeenLive: false, PendingDeadline: null, NoTransitions);

    /// <summary>
    /// Returns a copy recording a raw transition to <paramref name="newRaw"/> at
    /// <paramref name="at"/>: appends the instant (drop-oldest past
    /// <see cref="TransitionBound"/>), sets <see cref="LastRaw"/>, and resets
    /// <see cref="CurrentRunStartedAt"/> to the transition instant.
    /// </summary>
    internal NodeObservationHistory RecordTransition(HealthStatus newRaw, TimeSpan at)
    {
        var prior = Transitions;
        var priorCount = prior.Count;
        TimeSpan[] next;
        if (priorCount < TransitionBound)
        {
            next = new TimeSpan[priorCount + 1];
            for (var i = 0; i < priorCount; i++)
                next[i] = prior[i];
            next[priorCount] = at;
        }
        else
        {
            // Full: drop the oldest, shift left, append the new instant.
            next = new TimeSpan[TransitionBound];
            for (var i = 1; i < TransitionBound; i++)
                next[i - 1] = prior[i];
            next[TransitionBound - 1] = at;
        }

        return this with
        {
            LastRaw = newRaw,
            CurrentRunStartedAt = at,
            Transitions = next,
        };
    }
}

/// <summary>
/// Flap as a pure projection over a node's raw transition history (ADR-011 §8).
/// Flap is <em>not</em> a policy stage: it reads the raw transition record, so a
/// node that flaps constantly but is always suppressed still reports its flapping.
/// Obtain a history to project over from <see cref="HealthNode.Observe"/>.
/// </summary>
public static class FlapWindow
{
    /// <summary>
    /// Counts the raw status transitions in <paramref name="history"/> that fall
    /// within the half-open-below window <c>(now - window, now]</c> — a transition
    /// exactly <paramref name="window"/> old is counted, and future-stamped
    /// instants (only possible from a mis-ordered clock) are ignored.
    /// </summary>
    /// <param name="history">The node's observation history (raw transitions).</param>
    /// <param name="now">The current instant, in the same monotonic timebase as the history.</param>
    /// <param name="window">The look-back window; non-positive windows count nothing.</param>
    public static int Count(NodeObservationHistory history, TimeSpan now, TimeSpan window)
    {
        if (history is null)
            throw new ArgumentNullException(nameof(history));
        if (window <= TimeSpan.Zero)
            return 0;

        var cutoff = now - window;
        var count = 0;
        var transitions = history.Transitions;
        for (var i = 0; i < transitions.Count; i++)
        {
            var t = transitions[i];
            if (t >= cutoff && t <= now)
                count++;
        }

        return count;
    }
}
