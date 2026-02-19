using BenchmarkDotNet.Running;
using Prognosis.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(HealthGraphBenchmarks).Assembly).Run(args);
