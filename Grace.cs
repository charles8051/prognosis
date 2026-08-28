namespace Prognosis;

/// <summary>
/// The exported producer-side grace fold (ADR-011 §9) — Layer 1. A producer that
/// takes a <see cref="HealthLease"/> (leases and policies are mutually exclusive,
/// §7) but still wants grace-shaping can damp a freshly-sampled verdict <em>before</em>
/// <see cref="HealthLease.Affirm"/>, using the <b>same</b> grace core the in-graph
/// <c>WithGrace</c> policy runs. This is the pure fold, for a producer that wants to
/// own and persist its own <see cref="GraceState"/>; the state-owning
/// <see cref="GraceMachine"/> is the recommended surface for everyone else.
/// </summary>
public static class Grace
{
    /// <summary>
    /// Folds <paramref name="raw"/> through grace and returns both the verdict to
    /// <c>Affirm</c> and the <see cref="GraceState"/> to thread into the next call.
    /// Delegates to the same internal grace core as the <c>WithGrace</c> policy, so
    /// the two cannot diverge.
    /// </summary>
    /// <param name="raw">The freshly-sampled verdict.</param>
    /// <param name="isLiveNow">The domain liveness bit (§3) — only the producer knows it.</param>
    /// <param name="state">The grace bookkeeping to fold and thread forward.</param>
    /// <param name="options">The grace options; <see cref="GraceOptions.Deadline"/> is required.</param>
    /// <param name="now">
    /// The monotonic clock reading. Defaults to the library's own
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> conversion when
    /// <see langword="null"/> — so the sanctioned lock-free, side-effect-free,
    /// monotonic source is the easy path. Passing a wall clock satisfies the type
    /// but violates the discipline (ADR-011 §9); the library cannot validate it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/>.Deadline is negative.</exception>
    public static GraceResult ApplyGrace(
        HealthEvaluation raw,
        bool isLiveNow,
        GraceState state,
        GraceOptions options,
        TimeSpan? now = null)
    {
        _ = raw ?? throw new ArgumentNullException(nameof(raw));
        _ = options ?? throw new ArgumentNullException(nameof(options));
        GraceValidation.ValidateDeadline(options);

        return GraceCore.Apply(raw, isLiveNow, state, now ?? MonotonicClock.Now, options);
    }
}

/// <summary>
/// The recommended ergonomic grace surface (ADR-011 §9) — Layer 2. A thin stateful
/// wrapper over the same grace core that <b>owns</b> the <see cref="GraceState"/>
/// internally, so there is no caller-held state to drop, reset, or mis-thread. The
/// clock is read internally. Returns just the verdict to <c>Affirm</c>.
/// </summary>
public sealed class GraceMachine
{
    private readonly GraceOptions _options;
    private readonly object _lock = new();
    private GraceState _state;

    /// <summary>Creates a grace machine with the given options.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/>.Deadline is negative.</exception>
    public GraceMachine(GraceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        GraceValidation.ValidateDeadline(options);
        _state = default;
    }

    /// <summary>
    /// Folds <paramref name="raw"/> through grace, advancing the owned state, and
    /// returns the grace-adjusted verdict to <c>Affirm</c>. The clock is read
    /// internally. Thread-safe.
    /// </summary>
    /// <param name="raw">The freshly-sampled verdict.</param>
    /// <param name="isLiveNow">The domain liveness bit (§3).</param>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/> is null.</exception>
    public HealthEvaluation Update(HealthEvaluation raw, bool isLiveNow)
    {
        _ = raw ?? throw new ArgumentNullException(nameof(raw));
        lock (_lock)
        {
            var result = GraceCore.Apply(raw, isLiveNow, _state, MonotonicClock.Now, _options);
            _state = result.Next;
            return result.Effective;
        }
    }
}

internal static class GraceValidation
{
    internal static void ValidateDeadline(GraceOptions options)
    {
        if (options.Deadline < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.Deadline, "Grace Deadline must be non-negative.");
    }
}
