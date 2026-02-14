# Prognosis

**Unfiltered AI slop. Use at your own risk**

A dependency-aware service health modeling library for .NET. Models the health of multiple services as a directed graph where each service's effective status is computed from its own intrinsic health and the weighted health of its dependencies.

## Key concepts

### Health statuses

| Status | Value | Meaning |
|---|---|---|
| `Healthy` | 0 | Known good |
| `Unknown` | 1 | Not yet probed (startup state) |
| `Degraded` | 2 | Known partial failure |
| `Unhealthy` | 3 | Known failure |

Ordered worst-is-highest so comparisons naturally surface the most severe status.

### Dependency importance

| Importance | Propagation rule |
|---|---|
| `Required` | Dependency status passes through unchanged — an unhealthy dependency makes the parent unhealthy |
| `Important` | Unhealthy is capped at `Degraded` for the parent; `Unknown` and `Degraded` pass through |
| `Optional` | Dependency health is ignored entirely |

## Usage patterns

### 1. Implement `IServiceHealth` on a class you own

Embed a `ServiceHealthTracker` and delegate to it:

```csharp
class DatabaseService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health;

    public DatabaseService()
    {
        _health = new ServiceHealthTracker(
            () => IsConnected ? HealthStatus.Healthy : HealthStatus.Unhealthy);
    }

    public bool IsConnected { get; set; } = true;

    public string Name => "Database";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthStatus Evaluate() => _health.Evaluate();
}
```

### 2. Wrap a service you can't modify

Use `DelegatingServiceHealth` with a health-check delegate:

```csharp
var emailHealth = new DelegatingServiceHealth("EmailProvider",
    () => client.IsConnected ? HealthStatus.Healthy : HealthStatus.Unhealthy);
```

### 3. Pure composite aggregation

Create virtual aggregation points with no backing service:

```csharp
var app = new CompositeServiceHealth("Application",
[
    new ServiceDependency(authService, ServiceImportance.Required),
    new ServiceDependency(notifications, ServiceImportance.Important),
]);
```

## Graph operations

```csharp
// Evaluate a single service (walks its dependencies)
HealthStatus status = app.Evaluate();

// Snapshot the entire graph (depth-first post-order)
IReadOnlyList<ServiceSnapshot> snapshots = HealthAggregator.EvaluateAll(app);

// Package as a serialization-ready report with timestamp
HealthReport report = HealthAggregator.CreateReport(app);

// Detect circular dependencies
IReadOnlyList<IReadOnlyList<string>> cycles = HealthAggregator.DetectCycles(app);
```

## Observable health monitoring

All built-in types implement `IObservableServiceHealth`. Subscribe to individual services or monitor the full graph:

```csharp
// Individual service notifications
database.StatusChanged.Subscribe(observer);

// Graph-level polling with HealthMonitor
await using var monitor = new HealthMonitor([app], TimeSpan.FromSeconds(5));
monitor.ReportChanged.Subscribe(reportObserver);

// Manual poll (useful for testing or getting initial state)
monitor.Poll();
```

`IObservable<T>` is a BCL type — no System.Reactive dependency required. Add System.Reactive only when you want operators like `DistinctUntilChanged()` or `Throttle()`.

## Serialization

Both enums use `[JsonStringEnumConverter]` so they serialize as `"Healthy"` / `"Degraded"` / etc. The `HealthReport` and `ServiceSnapshot` records are designed as wire-friendly DTOs:

```json
{
  "Timestamp": "2026-02-13T18:30:00+00:00",
  "OverallStatus": "Healthy",
  "Services": [
    { "Name": "Database", "Status": "Healthy", "DependencyCount": 0 },
    { "Name": "AuthService", "Status": "Healthy", "DependencyCount": 2 }
  ]
}
```

## Project structure

| File | Purpose |
|---|---|
| `IServiceHealth.cs` | Core interface — `Name`, `Dependencies`, `Evaluate()` |
| `IObservableServiceHealth.cs` | Extends `IServiceHealth` with `StatusChanged` and `NotifyChanged()` |
| `HealthStatus.cs` | `Healthy` → `Unknown` → `Degraded` → `Unhealthy` enum |
| `ServiceImportance.cs` | `Required`, `Important`, `Optional` enum |
| `ServiceDependency.cs` | Record linking an `IServiceHealth` with its importance |
| `ServiceHealthTracker.cs` | Composable helper — embed in any class for dependency tracking, aggregation, and observability |
| `DelegatingServiceHealth.cs` | Adapter wrapping a `Func<HealthStatus>` for closed types |
| `CompositeServiceHealth.cs` | Pure aggregation point with no backing service |
| `HealthAggregator.cs` | Static helpers — `Aggregate`, `EvaluateAll`, `CreateReport`, `DetectCycles` |
| `HealthReport.cs` | Serialization-ready report DTO |
| `ServiceSnapshot.cs` | Serialization-ready per-service snapshot DTO |
| `HealthMonitor.cs` | Timer-based poller with `IObservable<HealthReport>` |

## Requirements

- .NET Standard 2.0 or .NET Standard 2.1 compatible runtime (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
- [System.Text.Json](https://www.nuget.org/packages/System.Text.Json) (bundled as a dependency)
- [Microsoft.Bcl.AsyncInterfaces](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces) (netstandard2.0 only, bundled as a dependency)
