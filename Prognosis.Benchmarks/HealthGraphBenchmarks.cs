using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Prognosis;

namespace Prognosis.Benchmarks;

/// <summary>
/// Benchmarks for the core Prognosis health graph operations on a realistic
/// 1000-node graph modeled as a layered microservice platform.
///
/// Run with:
///   dotnet run -c Release -- --filter *HealthGraphBenchmarks*
/// </summary>
[MemoryDiagnoser]
[ShortRunJob(RuntimeMoniker.Net10_0)]
public class HealthGraphBenchmarks
{
    private HealthNode _root = null!;
    private HealthNode _createRoot = null!;
    private HealthGraph _graph = null!;

    [Params(100)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = RealisticGraphBuilder.Build(NodeCount);
        _graph = HealthGraph.Create(_root);
    }

    [IterationSetup(Target = nameof(Create))]
    public void SetupCreate()
    {
        _createRoot = RealisticGraphBuilder.Build(NodeCount);
    }

    /// <summary>
    /// Measures <see cref="HealthGraph.Create"/> — stores the root and
    /// makes the graph available for queries.
    /// </summary>
    [Benchmark]
    public HealthGraph Create() => HealthGraph.Create(_createRoot);

    /// <summary>
    /// Measures root access — returns the stored root.
    /// </summary>
    [Benchmark]
    public HealthNode Root() => _graph.Root;

    /// <summary>
    /// Measures <see cref="HealthGraph.CreateReport"/> — returns the
    /// cached report without re-evaluation.
    /// </summary>
    [Benchmark]
    public HealthReport CreateReport() => _graph.CreateReport();

    /// <summary>
    /// Measures a single <see cref="HealthGraph.Evaluate(HealthNode)"/> call on the
    /// platform root — recursive aggregation through all dependencies.
    /// </summary>
    [Benchmark]
    public HealthEvaluation EvaluateRoot() => _graph.Evaluate(_root);

    /// <summary>
    /// Measures <see cref="HealthGraph.RefreshAll"/> — depth-first
    /// walk calling NotifyChangedCore on every node, rebuilds the
    /// cached report, and emits StatusChanged if it changed.
    /// </summary>
    [Benchmark]
    public HealthReport RefreshAll() => _graph.RefreshAll();

    /// <summary>
    /// Measures <see cref="HealthGraph.DetectCycles"/> — full DFS
    /// cycle detection over the graph.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<IReadOnlyList<string>> DetectCycles()
        => _graph.DetectCycles();
}
