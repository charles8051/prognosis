# Prognosis

**Unfiltered AI slop. Use at your own risk**

A dependency-aware service health modeling library for .NET. Models the health of multiple services as a directed graph where each service's effective status is computed from its own intrinsic health and the weighted health of its dependencies.

```mermaid
graph TD
    Store["🛒 Online Store"] -->|"Required"| Checkout["Checkout"]
    Store -->|"Important"| Search["Product Search"]
    Store -->|"Optional"| Reviews["Reviews"]
    Checkout -->|"Required"| Payment["Payment Gateway"]
    Checkout -->|"Required"| Inventory["Inventory"]
    Payment -->|"Important"| Fraud["Fraud Detection"]
    Search -->|"Required"| Index["Search Index"]

    style Store fill:#22c55e,color:#fff
    style Checkout fill:#22c55e,color:#fff
    style Payment fill:#22c55e,color:#fff
    style Inventory fill:#22c55e,color:#fff
    style Fraud fill:#22c55e,color:#fff
    style Search fill:#22c55e,color:#fff
    style Index fill:#22c55e,color:#fff
    style Reviews fill:#22c55e,color:#fff
```

> **How it works:** each service reports its own health and declares dependencies with an importance level. Prognosis walks the graph and computes the effective status — a **Required** dependency failing makes the parent unhealthy, an **Important** one degrades it, and an **Optional** one is ignored. If Fraud Detection goes down, Payment Gateway becomes *degraded*, which degrades Checkout, which degrades the whole store. If Payment Gateway itself goes down, Checkout becomes *unhealthy* — and since it's Required, the store is unhealthy too. If Reviews go down? Nothing happens.

## Packages

| Package | Purpose |
|---|---|
| [`Prognosis`](https://www.nuget.org/packages/Prognosis) | Core library — health graph modeling, aggregation, monitoring, serialization |
| [`Prognosis.Reactive`](https://www.nuget.org/packages/Prognosis.Reactive) | System.Reactive extensions — Rx-based polling, push-triggered reports, diff-based change streams |
| [`Prognosis.DependencyInjection`](https://www.nuget.org/packages/Prognosis.DependencyInjection) | Microsoft.Extensions.DependencyInjection integration — assembly scanning, fluent graph builder, hosted monitoring |

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
| `Resilient` | Like `Required`, but if at least one sibling `Resilient` dependency is healthy, unhealthy is capped at `Degraded`. All `Resilient` siblings must be unhealthy before the parent becomes unhealthy |

## Usage patterns

### 1. Implement `IHealthAware` on a class you own

Expose a `HealthNode` property — no forwarding boilerplate:

```csharp
class CacheService : IHealthAware
{
    public HealthNode HealthNode { get; }

    public CacheService()
    {
        HealthNode = new HealthAdapter("Cache",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Redis timeout"));
    }

    public bool IsConnected { get; set; } = true;
}
```

For services with fine-grained health attributes, use a `HealthGroup` backed by sub-nodes:

```csharp
class DatabaseService : IHealthAware
{
    public HealthNode HealthNode { get; }

    public bool IsConnected { get; set; } = true;
    public double AverageLatencyMs { get; set; } = 50;
    public double PoolUtilization { get; set; } = 0.3;

    public DatabaseService()
    {
        var connection = new HealthAdapter("Database.Connection",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));

        var latency = new HealthAdapter("Database.Latency",
            () => AverageLatencyMs switch
            {
                > 500 => new HealthEvaluation(HealthStatus.Degraded,
                    $"Avg latency {AverageLatencyMs:F0}ms exceeds 500ms threshold"),
                _ => HealthStatus.Healthy,
            });

        var connectionPool = new HealthAdapter("Database.ConnectionPool",
            () => PoolUtilization switch
            {
                >= 1.0 => new HealthEvaluation(HealthStatus.Unhealthy,
                    "Connection pool exhausted"),
                >= 0.9 => new HealthEvaluation(HealthStatus.Degraded,
                    $"Connection pool at {PoolUtilization:P0} utilization"),
                _ => HealthStatus.Healthy,
            });

        HealthNode = new HealthGroup("Database")
            .DependsOn(connection, Importance.Required)
            .DependsOn(latency, Importance.Important)
            .DependsOn(connectionPool, Importance.Required);
    }
}
```

The sub-nodes show up automatically in `EvaluateAll`, `CreateReport`, and the JSON output. Reason strings chain from the leaf all the way up:

```
Database.Latency: Degraded — Avg latency 600ms exceeds 500ms threshold
Database: Degraded — Database.Latency: Avg latency 600ms exceeds 500ms threshold
AuthService: Degraded — Database: Database.Latency: ...
```

### 2. Wrap a service you can't modify

Use `HealthAdapter` with a health-check delegate:

```csharp
var emailHealth = new HealthAdapter("EmailProvider",
    () => client.IsConnected
        ? HealthStatus.Healthy
        : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));
```

### 3. Compose the graph

Wire services together with `DependsOn`:

```csharp
var authService = new HealthAdapter("AuthService")
    .DependsOn(database.HealthNode, Importance.Required)
    .DependsOn(cache.HealthNode, Importance.Important);

var app = new HealthGroup("Application")
    .DependsOn(authService, Importance.Required)
    .DependsOn(notifications, Importance.Important);
```

### Resilient dependencies

Use `Importance.Resilient` when a parent has multiple paths to the same capability (e.g. primary + replica database). Losing one degrades — but doesn't kill — the parent:

```csharp
// If one goes down but the other is healthy, the parent is degraded (not unhealthy).
// If both go down, the parent becomes unhealthy.
var app = new HealthGroup("Application")
    .DependsOn(primaryDb, Importance.Resilient)
    .DependsOn(replicaDb, Importance.Resilient);
```

Only `Resilient`-marked siblings participate in the resilience check — `Required`, `Important`, and `Optional` dependencies are unaffected.

## Graph operations

```csharp
var graph = HealthGraph.Create(app);

// Evaluate a single service (walks its dependencies)
HealthEvaluation eval = app.Evaluate();

// Snapshot the entire graph (depth-first post-order)
IReadOnlyList<HealthSnapshot> snapshots = graph.EvaluateAll();

// Package as a serialization-ready report with timestamp
HealthReport report = graph.CreateReport();

// Detect circular dependencies
IReadOnlyList<IReadOnlyList<string>> cycles = graph.DetectCycles();

// Diff two reports to find individual service changes
IReadOnlyList<StatusChange> changes = before.DiffTo(after);
```

## Observable health monitoring

Every `HealthNode` node supports push-based notifications. Subscribe to individual services or monitor the full graph:

```csharp
// Individual service notifications
database.HealthNode.StatusChanged.Subscribe(observer);

// Graph-level polling with HealthMonitor
await using var monitor = new HealthMonitor(graph, TimeSpan.FromSeconds(5));
monitor.Start();
monitor.ReportChanged.Subscribe(reportObserver);

// Manual poll (useful for testing or getting initial state)
monitor.Poll();
```

`IObservable<T>` is a BCL type — no System.Reactive dependency required. Add System.Reactive only when you want operators like `DistinctUntilChanged()` or `Throttle()`.

## Dependency injection

The `Prognosis.DependencyInjection` package provides a fluent builder for configuring the health graph within a hosted application:

```csharp
builder.Services.AddPrognosis(health =>
{
    // Discover all IHealthAware implementations and wire [DependsOn<T>] attributes.
    health.ScanForServices(typeof(Program).Assembly);

    // Wrap a third-party service with a health delegate.
    // Name defaults to typeof(T).Name when omitted.
    health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
        client => client.IsConnected
            ? HealthStatus.Healthy
            : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP refused"));

    // Define composite aggregation nodes.
    health.AddComposite(ServiceNames.NotificationSystem, n =>
    {
        n.DependsOn(nameof(MessageQueueService), Importance.Required);
        n.DependsOn("EmailProvider", Importance.Optional);
    });

    health.AddComposite(ServiceNames.Application, app =>
    {
        app.DependsOn<AuthService>(Importance.Required);
        app.DependsOn(ServiceNames.NotificationSystem, Importance.Important);
    });

    // Designate the root of the graph. When only one root is declared,
    // a plain HealthGraph singleton is registered.
    health.MarkAsRoot(ServiceNames.Application);

    health.UseMonitor(TimeSpan.FromSeconds(30));
});
```

#### Multiple roots (shared nodes, separate graphs)

Call `MarkAsRoot` more than once to materialize several graphs from a single shared node pool. Each graph is registered as a keyed `HealthGraph` (keyed by the root name). Use the generic `MarkAsRoot<T>()` overload to also register a strongly-typed `HealthGraph<T>` for consumers that don't have keyed service support:

```csharp
builder.Services.AddPrognosis(health =>
{
    health.ScanForServices(typeof(Program).Assembly);

    health.AddComposite(ServiceNames.OpsDashboard, ops =>
    {
        ops.DependsOn<DatabaseService>(Importance.Required);
        ops.DependsOn<CacheService>(Importance.Required);
    });

    health.AddComposite(ServiceNames.CustomerView, cust =>
    {
        cust.DependsOn<AuthService>(Importance.Required);
    });

    // Each MarkAsRoot call produces a separate HealthGraph.
    // Nodes (e.g. DatabaseService) are shared across graphs.
    health.MarkAsRoot<OpsDashboard>();      // registers keyed + HealthGraph<OpsDashboard>
    health.MarkAsRoot<CustomerView>();      // registers keyed + HealthGraph<CustomerView>
});

// Keyed resolution (requires Microsoft.Extensions.DependencyInjection 8+):
var opsGraph    = sp.GetRequiredKeyedService<HealthGraph>("OpsDashboard");
var custGraph   = sp.GetRequiredKeyedService<HealthGraph>("CustomerView");

// Generic resolution (works on any DI container):
var opsGraph    = sp.GetRequiredService<HealthGraph<OpsDashboard>>().Graph;
var custGraph   = sp.GetRequiredService<HealthGraph<CustomerView>>().Graph;
```

Declare dependency edges on classes you own with attributes:

```csharp
[DependsOn<DatabaseService>(Importance.Required)]
[DependsOn<CacheService>(Importance.Important)]
class AuthService : IHealthAware
{
    public HealthNode HealthNode { get; } = new HealthAdapter("AuthService");
}
```

Inject `HealthGraph` to access the materialized graph at runtime:

```csharp
var graph = serviceProvider.GetRequiredService<HealthGraph>();
var report = graph.CreateReport();

// Type-safe lookup — uses typeof(AuthService).Name as key.
if (graph.TryGetNode<AuthService>(out var auth))
    Console.WriteLine($"AuthService has {auth.Dependencies.Count} deps");

// String-based lookup still available.
HealthNode dbService = graph["Database"];
```

## Reactive extensions

The `Prognosis.Reactive` package provides Rx-based alternatives to polling. All extensions work on both `HealthGraph` (whole graph) and `HealthNode` (single subtree):

```csharp
var graph = HealthGraph.Create(app);

// Timer-driven polling — emits HealthReport on change.
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report => Console.WriteLine(report.OverallStatus));

// Push-triggered — reacts to StatusChanged events from the root, no polling delay.
graph.ObserveHealthReport()
    .Subscribe(report => Console.WriteLine(report.OverallStatus));

// Diff-based change stream — composable with any report source.
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectServiceChanges()
    .Subscribe(change =>
        Console.WriteLine($"{change.Name}: {change.Previous} → {change.Current}"));

// Per-node evaluation stream.
database.HealthNode.ObserveStatus()
    .Subscribe(eval => Console.WriteLine($"Database: {eval.Status}"));
```

### Sharing streams across subscribers

The Rx helpers produce cold observables — each subscription runs its own pipeline. To share a single evaluation across multiple subscribers:

```csharp
// Auto-stop when last subscriber unsubscribes.
var shared = graph.CreateSharedReportStream(TimeSpan.FromSeconds(30));

// Replay latest report to late subscribers.
var shared = graph.CreateSharedReportStream(TimeSpan.FromSeconds(30),
    ShareStrategy.ReplayLatest);

// Push-triggered variant.
var shared = graph.CreateSharedObserveStream();
```

Or use standard Rx multicast operators directly: `Publish().RefCount()` or `Replay(1).RefCount()`.

## Serialization

Both enums use `[JsonStringEnumConverter]` so they serialize as `"Healthy"` / `"Degraded"` / etc. The `HealthReport` and `HealthSnapshot` records are designed as wire-friendly DTOs:

```json
{
  "Timestamp": "2026-02-13T18:30:00+00:00",
  "OverallStatus": "Healthy",
  "Services": [
    { "Name": "Database.Connection", "Status": "Healthy" },
    { "Name": "Database.Latency", "Status": "Healthy" },
    { "Name": "Database.ConnectionPool", "Status": "Healthy" },
    { "Name": "Database", "Status": "Healthy" },
    { "Name": "AuthService", "Status": "Healthy" }
  ]
}
```

## Project structure

### Core (`Prognosis`)

| File | Purpose |
|---|---|
| `HealthNode.cs` | Abstract base class — `Name`, `Dependencies`, `Evaluate()`, `StatusChanged`, `BubbleChange()`, `DependsOn()` |
| `IHealthAware.cs` | Marker interface — implement on your classes with a single `HealthNode` property |
| `HealthStatus.cs` | `Healthy` → `Unknown` → `Degraded` → `Unhealthy` enum |
| `HealthEvaluation.cs` | Status + optional reason pair, with implicit conversion from `HealthStatus` |
| `Importance.cs` | `Required`, `Important`, `Optional`, `Resilient` enum |
| `HealthDependency.cs` | Record linking a `HealthNode` with its importance |
| `HealthAdapter.cs` | Wraps a `Func<HealthEvaluation>` — use for services with intrinsic health checks |
| `HealthGroup.cs` | Pure aggregation point — health derived entirely from dependencies |
| `HealthReport.cs` | Serialization-ready report DTO with `DiffTo` for change detection |
| `HealthSnapshot.cs` | Serialization-ready per-service snapshot DTO |
| `StatusChange.cs` | Record describing a single service's status transition |
| `HealthReportComparer.cs` | `IEqualityComparer<HealthReport>` for deduplicating reports |
| `HealthMonitor.cs` | Timer-based poller with `IObservable<HealthReport>` |

### Reactive extensions (`Prognosis.Reactive`)

| File | Purpose |
|---|---|
| `HealthRxExtensions.cs` | `PollHealthReport`, `ObserveHealthReport`, `SelectServiceChanges` |
| `HealthRxShared.cs` | `CreateSharedReportStream`, `CreateSharedObserveStream`, `ShareStrategy` |

### Dependency injection (`Prognosis.DependencyInjection`)

| File | Purpose |
|---|---|
| `ServiceCollectionExtensions.cs` | `AddPrognosis` entry point — assembly scanning and graph materialization |
| `PrognosisBuilder.cs` | Fluent builder — `ScanForServices`, `AddDelegate<T>`, `AddComposite`, `MarkAsRoot` |
| `DependencyConfigurator.cs` | Fluent edge declaration — `DependsOn<T>`, `DependsOn(name)` |
| `DependsOnAttribute.cs` | `[DependsOn<T>]` attribute for declarative dependency edges |
| `HealthGraph.cs` | Type forwarder for core `HealthGraph` (`Root`, indexer, `CreateReport()`) |
| `HealthGraphOfT.cs` | `HealthGraph<TRoot>` typed wrapper for multi-root DI resolution |
| `PrognosisMonitorExtensions.cs` | `UseMonitor` extension + `IHostedService` adapter |

## Requirements

- .NET Standard 2.0 or .NET Standard 2.1 compatible runtime (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
- [System.Text.Json](https://www.nuget.org/packages/System.Text.Json) (bundled as a dependency)
- [Microsoft.Bcl.AsyncInterfaces](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces) (netstandard2.0 only, bundled as a dependency)
