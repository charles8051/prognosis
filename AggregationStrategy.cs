namespace Prognosis;

/// <summary>
/// A function that combines a service's intrinsic health with its dependency
/// evaluations to produce an effective <see cref="HealthEvaluation"/>.
/// </summary>
/// <param name="intrinsic">The service's own health (independent of dependencies).</param>
/// <param name="dependencies">The service's dependency edges.</param>
/// <returns>The effective health evaluation for the service.</returns>
/// <remarks>
/// The default strategy is <see cref="HealthAggregator.Aggregate"/>.
/// A built-in alternative is <see cref="HealthAggregator.AggregateWithRedundancy"/>,
/// which treats a single unhealthy dependency as degraded when healthy siblings exist.
/// </remarks>
public delegate HealthEvaluation AggregationStrategy(
    HealthEvaluation intrinsic,
    IReadOnlyList<ServiceDependency> dependencies);
