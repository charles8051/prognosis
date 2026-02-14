# Prognosis.Reactive

System.Reactive extensions for the [Prognosis](https://www.nuget.org/packages/Prognosis) service health graph. Provides Rx-based alternatives to the polling-based `HealthMonitor` in the core package.

## Installation

```
dotnet add package Prognosis.Reactive
```

## API

### `PollHealthReport` — timer-driven polling

Polls the full health graph on a fixed interval, notifying all observable services and emitting a `HealthReport` whenever the graph state changes:

```csharp
using Prognosis.Reactive;

var roots = new IServiceHealth[] { app };

roots.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));
```

### `ObserveHealthReport` — push-triggered evaluation

Reacts to `StatusChanged` events from leaf nodes (services with no dependencies), throttles to avoid evaluation storms, then runs a single-pass graph evaluation:

```csharp
roots.ObserveHealthReport(TimeSpan.FromMilliseconds(500))
    .Subscribe(report =>
        Console.WriteLine($"Overall: {report.OverallStatus}"));
```

Only leaf nodes are observed as triggers since parent status changes are always a consequence of `HealthAggregator.NotifyGraph`, not exogenous events.

### `SelectServiceChanges` — diff-based change stream

Projects consecutive `HealthReport` emissions into individual `ServiceStatusChange` events by diffing the reports. Composable with any report source:

```csharp
roots.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectServiceChanges()
    .Subscribe(change =>
        Console.WriteLine($"{change.Name}: {change.Previous} → {change.Current}"));
```

Each `ServiceStatusChange` includes the service name, previous status, current status, and optional reason — derived from `HealthAggregator.Diff` in the core library.

## Dependencies

- [Prognosis](https://www.nuget.org/packages/Prognosis) (core library)
- [System.Reactive](https://www.nuget.org/packages/System.Reactive) >= 6.0.1

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
