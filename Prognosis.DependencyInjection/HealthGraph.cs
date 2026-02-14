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
    private readonly Dictionary<string, IServiceHealth> _services;

    internal HealthGraph(IServiceHealth[] roots, Dictionary<string, IServiceHealth> services)
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
    public IServiceHealth[] Roots { get; }

    /// <summary>
    /// Looks up any node in the graph by its <see cref="IServiceHealth.Name"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// No service with the given name exists in the graph.
    /// </exception>
    public IServiceHealth this[string name] => _services[name];

    /// <summary>
    /// Attempts to look up a node by name, returning <see langword="false"/>
    /// if no service with the given name exists.
    /// </summary>
    public bool TryGetService(string name, out IServiceHealth service) =>
        _services.TryGetValue(name, out service!);

    /// <summary>
    /// All named services in the graph (leaves, composites, and delegates).
    /// </summary>
    public IEnumerable<IServiceHealth> Services => _services.Values;

    /// <summary>Convenience: creates a point-in-time report from all roots.</summary>
    public HealthReport CreateReport() => HealthAggregator.CreateReport(Roots);
}
