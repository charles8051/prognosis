using System;

namespace Prognosis;

public static class HealthStatusExtensions
{
    public static bool IsWorseThan(this HealthStatus status, HealthStatus other)
        => Rank(status) > Rank(other);

    public static HealthStatus Worst(HealthStatus a, HealthStatus b)
        => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(HealthStatus status) => status switch
    {
        HealthStatus.Healthy   => 0,
        HealthStatus.Unknown   => 1,
        HealthStatus.Degraded  => 2,
        HealthStatus.Unhealthy => 3,

        // No silent default: the old `_ => int.MaxValue` ranked an unmapped status worse than
        // Unhealthy, silently making it the worst everywhere it was folded (ADR-008 §4).
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            $"Unhandled {nameof(HealthStatus)} value. A new member was added without updating "
                + $"{nameof(HealthStatusExtensions)}.{nameof(Rank)}; see ADR-008."),
    };
}