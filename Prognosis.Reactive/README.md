# Prognosis.Reactive

System.Reactive extensions for the [Prognosis](https://www.nuget.org/packages/Prognosis) service health graph. Provides Rx-based alternatives to the polling-based `HealthMonitor` in the core package.

## Installation

```
dotnet add package Prognosis.Reactive
```

## API

All extensions operate on `HealthGraph`. Use `HealthGraph.Create(root)` to materialize the graph, then chain Rx operators.

### `PollHealthReport` — timer-driven polling

Polls on a fixed interval, re-evaluates every node, and emits a `HealthReport` whenever the graph state changes:

```csharp
using Prognosis.Reactive;

var graph = HealthGraph.Create(app);
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report =>
        Console.WriteLine($"{report.Nodes.Count} nodes"));
```

### `ObserveHealthReport` — push-triggered evaluation

Reacts to `HealthGraph.StatusChanged` events and produces a fresh report immediately — no polling delay. Changes in any transitive dependency bubble up automatically.

```csharp
graph.ObserveHealthReport()
    .Subscribe(report =>
        Console.WriteLine($"{report.Nodes.Count} nodes"));
```

### `SelectHealthChanges` — diff-based change stream

Projects consecutive `HealthReport` emissions into individual `StatusChange` events by diffing the reports. Composable with any report source:

```csharp
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectHealthChanges()
    .Subscribe(change =>
        Console.WriteLine($"{change.Name}: {change.Previous} → {change.Current}"));
```

Each `StatusChange` includes the service name, previous status, current status, and optional reason — derived from `HealthReport.DiffTo` in the core library.

### Sharing streams across subscribers

The Rx helpers produce cold `IObservable<HealthReport>` streams — each subscription runs an independent pipeline and triggers its own evaluations. To share a single evaluation across multiple subscribers, use standard Rx multicast operators directly: `Publish().RefCount()` or `Replay(1).RefCount()`.

## Dependencies

- [Prognosis](https://www.nuget.org/packages/Prognosis) (core library)
- [System.Reactive](https://www.nuget.org/packages/System.Reactive) >= 6.0.1

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
