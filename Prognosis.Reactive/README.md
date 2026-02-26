# Prognosis.Reactive

System.Reactive extensions for the [Prognosis](https://www.nuget.org/packages/Prognosis) service health graph. Provides Rx-based alternatives to the polling-based `HealthMonitor` in the core package.

## Installation

```
dotnet add package Prognosis.Reactive
```

## API

All extensions are available on both `HealthGraph` (whole graph) and `HealthNode` (single subtree). Use `HealthGraph` when you want a report covering all roots; use `HealthNode` when you only care about one service and its dependencies.

### `PollHealthReport` — timer-driven polling

Polls on a fixed interval, re-evaluates every node, and emits a `HealthReport` whenever the graph state changes:

```csharp
using Prognosis.Reactive;

// Whole graph — calls NotifyAll() + CreateReport() on each tick.
var graph = HealthGraph.Create(app);
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));

// Single subtree — calls NotifyDescendants() + CreateReport() on the node.
app.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));
```

### `ObserveHealthReport` — push-triggered evaluation

Reacts to `StatusChanged` events and produces a fresh report immediately — no polling delay. Changes in any transitive dependency bubble up to the subscribed node/root automatically.

```csharp
// Whole graph — merges StatusChanged from all roots.
graph.ObserveHealthReport()
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));

// Single subtree — subscribes to the node's own StatusChanged stream.
app.ObserveHealthReport()
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));
```

### `ObserveStatus` — per-node evaluation stream

Emits the node's `HealthEvaluation` (status + reason) each time its effective health changes:

```csharp
database.HealthNode.ObserveStatus()
    .Subscribe(eval =>
        Console.WriteLine($"Database: {eval.Status} — {eval.Reason}"));
```

### `SelectServiceChanges` — diff-based change stream

Projects consecutive `HealthReport` emissions into individual `StatusChange` events by diffing the reports. Composable with any report source:

```csharp
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectServiceChanges()
    .Subscribe(change =>
        Console.WriteLine($"{change.Name}: {change.Previous} → {change.Current}"));
```

Each `StatusChange` includes the service name, previous status, current status, and optional reason — derived from `HealthReport.DiffTo` in the core library.

### Sharing streams across subscribers

The Rx helpers produce cold `IObservable<HealthReport>` streams — each subscription runs an independent pipeline and triggers its own evaluations. To share a single evaluation across multiple subscribers:

```csharp
// Convenience helpers — available on both HealthGraph and HealthNode.
var shared = graph.CreateSharedReportStream(TimeSpan.FromSeconds(30));
var shared = graph.CreateSharedObserveStream();

// With replay for late subscribers:
var shared = graph.CreateSharedReportStream(TimeSpan.FromSeconds(30),
    ShareStrategy.ReplayLatest);
```

Or use standard Rx multicast operators directly: `Publish().RefCount()` or `Replay(1).RefCount()`.

## Dependencies

- [Prognosis](https://www.nuget.org/packages/Prognosis) (core library)
- [System.Reactive](https://www.nuget.org/packages/System.Reactive) >= 6.0.1

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
