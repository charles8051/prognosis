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
[SimpleJob(RuntimeMoniker.Net10_0)]
public class HealthGraphBenchmarks
{
    private HealthNode _root = null!;
    private HealthGraph _graph = null!;

    [Params(100, 1000)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = RealisticGraphBuilder.Build(NodeCount);
        _graph = HealthGraph.Create(_root);
    }

    /// <summary>
    /// Measures <see cref="HealthGraph.Create"/> — stores the root and
    /// makes the graph available for queries.
    /// </summary>
    [Benchmark]
    public HealthGraph Create() => HealthGraph.Create(_root);

    /// <summary>
    /// Measures root access — returns the stored root array.
    /// </summary>
    [Benchmark]
    public HealthNode[] Roots() => _graph.Roots;

    /// <summary>
    /// Measures <see cref="HealthGraph.EvaluateAll"/> — depth-first
    /// walk producing a <see cref="HealthSnapshot"/> for every node.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<HealthSnapshot> EvaluateAll()
        => _graph.EvaluateAll();

    /// <summary>
    /// Measures <see cref="HealthGraph.CreateReport"/> — EvaluateAll +
    /// overall status computation + report construction.
    /// </summary>
    [Benchmark]
    public HealthReport CreateReport() => _graph.CreateReport();

    /// <summary>
    /// Measures a single <see cref="HealthNode.Evaluate"/> call on the
    /// platform root — recursive aggregation through all dependencies.
    /// </summary>
    [Benchmark]
    public HealthEvaluation EvaluateRoot() => _root.Evaluate();

    /// <summary>
    /// Measures <see cref="HealthGraph.NotifyAll"/> — depth-first
    /// walk calling NotifyChanged on every node.
    /// </summary>
    [Benchmark]
    public void NotifyGraph() => _graph.NotifyAll();

    /// <summary>
    /// Measures <see cref="HealthGraph.DetectCycles"/> — full DFS
    /// cycle detection over the graph.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<IReadOnlyList<string>> DetectCycles()
        => _graph.DetectCycles();
}
