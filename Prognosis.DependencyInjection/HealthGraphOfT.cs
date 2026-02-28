namespace Prognosis.DependencyInjection;

/// <summary>
/// A typed wrapper around <see cref="HealthGraph"/> that enables resolving
/// a specific graph instance from dependency injection when multiple graphs
/// are registered.
/// <para>
/// Use <see cref="PrognosisBuilder.MarkAsRoot{T}"/> during configuration to
/// register a <see cref="HealthGraph{TRoot}"/>. Then resolve it from the
/// service provider:
/// <code>
/// var graph = sp.GetRequiredService&lt;HealthGraph&lt;MyRootMarker&gt;&gt;();
/// graph.Root …
/// </code>
/// </para>
/// </summary>
/// <typeparam name="TRoot">
/// A marker type whose <see cref="System.Type.Name"/> identifies the root node
/// in the health graph. This is the same type passed to
/// <see cref="PrognosisBuilder.MarkAsRoot{T}"/>.
/// </typeparam>
public sealed class HealthGraph<TRoot> where TRoot : class
{
    private readonly HealthGraph _graph;

    internal HealthGraph(HealthGraph graph) => _graph = graph;

    /// <summary>The underlying <see cref="HealthGraph"/>.</summary>
    public HealthGraph Graph => _graph;

    /// <inheritdoc cref="HealthGraph.Root"/>
    public HealthNode Root => _graph.Root;

    /// <inheritdoc cref="HealthGraph.Nodes"/>
    public IEnumerable<HealthNode> Nodes => _graph.Nodes;

    /// <inheritdoc cref="HealthGraph.CreateReport"/>
    public HealthReport CreateReport() => _graph.CreateReport();

    /// <inheritdoc cref="HealthGraph.EvaluateAll"/>
    public IReadOnlyList<HealthSnapshot> EvaluateAll() => _graph.EvaluateAll();

    /// <inheritdoc cref="HealthGraph.StatusChanged"/>
    public IObservable<HealthReport> StatusChanged => _graph.StatusChanged;

    /// <inheritdoc cref="HealthGraph.Evaluate(string)"/>
    public HealthEvaluation Evaluate(string name) => _graph.Evaluate(name);

    /// <inheritdoc cref="HealthGraph.Refresh(HealthNode)"/>
    public void Refresh(HealthNode node) => _graph.Refresh(node);

    /// <inheritdoc cref="HealthGraph.Refresh(string)"/>
    public void Refresh(string name) => _graph.Refresh(name);

    /// <inheritdoc cref="HealthGraph.RefreshAll"/>
    public void RefreshAll() => _graph.RefreshAll();

    /// <summary>
    /// Converts a <see cref="HealthGraph{TRoot}"/> to its underlying
    /// <see cref="HealthGraph"/> implicitly.
    /// </summary>
    public static implicit operator HealthGraph(HealthGraph<TRoot> typed) => typed._graph;
}
