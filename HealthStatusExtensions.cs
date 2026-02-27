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
        _ => int.MaxValue,
    };
}