namespace Prognosis.DependencyInjection;

/// <summary>
/// The materialized health graph, available from DI. Serves as the entry point
/// for report generation, monitoring, and Rx pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Inject this from the service provider to access the graph roots and look up
/// individual services by name. The <see cref="Roots"/> array is directly
/// compatible with the Rx extensions in <c>Prognosis.Reactive</c>:
/// </para>
/// <code>
/// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
/// graph.Roots.PollHealthReport(TimeSpan.FromSeconds(30)).Subscribe(...);
/// </code>
/// </remarks>
public sealed class HealthGraph
{
    private readonly Dictionary<string, ServiceHealth> _services;

    internal HealthGraph(ServiceHealth[] roots, Dictionary<string, ServiceHealth> services)
    {
        Roots = roots;
        _services = services;
    }

    /// <summary>
    /// The top-level graph entry points. Pass directly to
    /// <see cref="HealthAggregator.CreateReport"/>,
    /// <see cref="HealthAggregator.EvaluateAll"/>, or the Rx
    /// <c>PollHealthReport</c> / <c>ObserveHealthReport</c> extensions.
    /// </summary>
    public ServiceHealth[] Roots { get; }

    /// <summary>
    /// Looks up any node in the graph by its <see cref="ServiceHealth.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public ServiceHealth this[string name] => _services[name];

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no service with the given name exists.
    /// </summary>
    public bool TryGetService(string name, out ServiceHealth service) =>
        _services.TryGetValue(name, out service!);

    /// <summary>
    /// Looks up a service whose <see cref="ServiceHealth.Name"/> matches
    /// <c>typeof(T).Name</c>. This is a convenience for the common convention
    /// where service names are derived from their concrete types.
    /// </summary>
    /// <typeparam name="T">
    /// The type whose <see cref="System.Type.Name"/> is used as the lookup key.
    /// </typeparam>
    public bool TryGetService<T>(out ServiceHealth service) where T : class, IServiceHealth =>
        _services.TryGetValue(typeof(T).Name, out service!);

    /// <summary>
    /// All named services in the graph (leaves, composites, and delegates).
    /// </summary>
    public IEnumerable<ServiceHealth> Services => _services.Values;

    /// <summary>Convenience: creates a point-in-time report from all roots.</summary>
    public HealthReport CreateReport() => HealthAggregator.CreateReport(Roots);
}
