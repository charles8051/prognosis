# Prognosis

A dependency-aware service health modeling library for .NET.

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

### 1. Expose `HealthNode` properties on a class you own

No base class or interface required — just a public `HealthNode` property:

```csharp
class CacheService
{
    public HealthNode HealthNode { get; }

    public CacheService()
    {
        HealthNode = HealthNode.CreateDelegate("Cache",
            () => IsConnected
                ? HealthStatus.Healthy
                : HealthEvaluation.Unhealthy("Redis timeout"));
    }

    public bool IsConnected { get; set; } = true;
}
```

For services with fine-grained health attributes, use `HealthNode.CreateComposite` backed by sub-nodes:

```csharp
class DatabaseService
{
    public HealthNode HealthNode { get; }

    public bool IsConnected { get; set; } = true;
    public double AverageLatencyMs { get; set; } = 50;
    public double PoolUtilization { get; set; } = 0.3;

    public DatabaseService()
    {
        var connection = HealthNode.CreateDelegate("Database.Connection",
            () => IsConnected
                ? HealthStatus.Healthy
                : HealthEvaluation.Unhealthy("Connection lost"));

        var latency = HealthNode.CreateDelegate("Database.Latency",
            () => AverageLatencyMs switch
            {
                > 500 => HealthEvaluation.Degraded(
                    $"Avg latency {AverageLatencyMs:F0}ms exceeds 500ms threshold"),
                _ => HealthStatus.Healthy,
            });

        var connectionPool = HealthNode.CreateDelegate("Database.ConnectionPool",
            () => PoolUtilization switch
            {
                >= 1.0 => HealthEvaluation.Unhealthy(
                    "Connection pool exhausted"),
                >= 0.9 => HealthEvaluation.Degraded(
                    $"Connection pool at {PoolUtilization:P0} utilization"),
                _ => HealthStatus.Healthy,
            });

        HealthNode = HealthNode.CreateComposite("Database")
            .DependsOn(connection, Importance.Required)
            .DependsOn(latency, Importance.Important)
            .DependsOn(connectionPool, Importance.Required);
    }
}
```

The sub-nodes show up automatically in `GetReport`, `RefreshAll`, and the JSON output.

```
Database.Latency: Degraded — Avg latency 600ms exceeds 500ms threshold
Database: Degraded — Database.Latency: Avg latency 600ms exceeds 500ms threshold
AuthService: Degraded — Database: Database.Latency: ...
```

### 2. Wrap a service you can't modify

Use `HealthNode.CreateDelegate` with a health-check delegate:

```csharp
var emailHealth = HealthNode.CreateDelegate("EmailProvider",
    () => client.IsConnected
        ? HealthStatus.Healthy
        : HealthEvaluation.Unhealthy("SMTP connection refused"));
```

### 3. Compose the graph

Wire services together with `DependsOn`:

```csharp
var authService = HealthNode.CreateDelegate("AuthService")
    .DependsOn(database.HealthNode, Importance.Required)
    .DependsOn(cache.HealthNode, Importance.Important);

var app = HealthNode.CreateComposite("Application")
    .DependsOn(authService, Importance.Required)
    .DependsOn(notifications, Importance.Important);
```

### Resilient dependencies

Use `Importance.Resilient` when a parent has multiple paths to the same capability (e.g. primary + replica database). Losing one degrades — but doesn't kill — the parent:

```csharp
// If one goes down but the other is healthy, the parent is degraded (not unhealthy).
// If both go down, the parent becomes unhealthy.
var app = HealthNode.CreateComposite("Application")
    .DependsOn(primaryDb, Importance.Resilient)
    .DependsOn(replicaDb, Importance.Resilient);
```

Only `Resilient`-marked siblings participate in the resilience check — `Required`, `Important`, and `Optional` dependencies are unaffected.

## Graph operations

```csharp
var graph = HealthGraph.Create(app);

// Re-evaluate all intrinsic checks and return a fresh report
HealthReport report = graph.RefreshAll();

// The root's aggregated status
HealthSnapshot root = report.Root;

// Return the cached report (cheap, no re-evaluation)
HealthReport cached = graph.GetReport();

// Refresh a single node (re-evaluates intrinsic check, propagates upward, emits StatusChanged)
app.Refresh();

// Detect circular dependencies
IReadOnlyList<IReadOnlyList<string>> cycles = graph.DetectCycles();

// Diff two reports to find individual service changes
IReadOnlyList<StatusChange> changes = before.DiffTo(after);
```

## Cross-node health reporting

When a node detects a failure that actually belongs to a different node (root-cause attribution), use `ReportStatus` to push the failure to the correct origin. This ensures all dependents of the origin node are notified via normal propagation — not just the node that detected the problem.

```csharp
var internet = HealthNode.CreateDelegate("Internet");
var api = HealthNode.CreateDelegate("API")
    .DependsOn(internet, Importance.Required);
var cache = HealthNode.CreateDelegate("Cache")
    .DependsOn(internet, Importance.Required);
```

When the API service detects a connectivity failure, it reports the failure on the Internet node:

```csharp
// In the API service's operational code (not the health delegate):
catch (HttpRequestException ex) when (IsConnectivityError(ex))
{
    internet.ReportStatus(HealthEvaluation.Unhealthy("Connectivity lost"));
    // → Internet becomes Unhealthy
    // → API and Cache both become Unhealthy via propagation
}
```

The reported status acts as the intrinsic evaluation until the next delegate-based refresh (poll tick or explicit `Refresh`) naturally replaces it — no manual expiration needed.

## Observable health monitoring

`HealthGraph` exposes push-based observables for health state changes and topology mutations:

```csharp
// Graph-level status changes — emits a HealthReport whenever the effective state changes.
graph.StatusChanged.Subscribe(reportObserver);

// Topology changes — emits when nodes are added or removed.
graph.TopologyChanged.Subscribe(topoObserver);

// Timer-based polling with HealthMonitor
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
    // Auto-generated — discovers all classes with public HealthNode properties
    // and wires [DependsOn] attribute-declared edges.
    health.AddDiscoveredNodes();

    // Wrap a third-party service with a health delegate.
    // Name defaults to typeof(T).Name when omitted.
    health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
        client => client.IsConnected
            ? HealthStatus.Healthy
            : HealthEvaluation.Unhealthy("SMTP refused"));

    // Define composite aggregation nodes.
    health.AddComposite("NotificationSystem", n =>
    {
        n.DependsOn("MessageQueueService", Importance.Required);
        n.DependsOn("EmailProvider", Importance.Optional);
    });

    health.AddComposite("Application", app =>
    {
        app.DependsOn("AuthService", Importance.Required);
        app.DependsOn("NotificationSystem", Importance.Important);
    });

    // Designate the root of the graph. When only one root is declared,
    // a plain HealthGraph singleton is registered.
    health.MarkAsRoot("Application");

    health.UseMonitor(TimeSpan.FromSeconds(30));
});
```

#### Multiple roots (shared nodes, separate graphs)

Call `MarkAsRoot` more than once to materialize several graphs from a single shared node pool. Each graph is registered as a keyed `HealthGraph` (keyed by the root name). Use the generic `MarkAsRoot<T>()` overload to also register a strongly-typed `HealthGraph<T>` for consumers that don't have keyed service support:

```csharp
builder.Services.AddPrognosis(health =>
{
    health.AddDiscoveredNodes();

    health.AddComposite("OpsDashboard", ops =>
    {
        ops.DependsOn("Database", Importance.Required);
        ops.DependsOn("Cache", Importance.Required);
    });

    health.AddComposite("CustomerView", cust =>
    {
        cust.DependsOn("AuthService", Importance.Required);
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

Declare dependency edges on `HealthNode` properties with attributes:

```csharp
class AuthService
{
    [DependsOn("Database", Importance.Required)]
    [DependsOn("Cache", Importance.Important)]
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("AuthService");
}
```

Inject `HealthGraph` to access the materialized graph at runtime:

```csharp
var graph = serviceProvider.GetRequiredService<HealthGraph>();
var report = graph.GetReport();

// Type-safe lookup
if (graph.TryGetNode<AuthService>(out var auth))
    Console.WriteLine($"AuthService has {auth.Dependencies.Count} deps");

// String-based lookup still available.
HealthNode dbService = graph["Database"];
```

## Reactive extensions

The `Prognosis.Reactive` package provides Rx-based alternatives to polling. All extensions operate on `HealthGraph`:

```csharp
var graph = HealthGraph.Create(app);

// Timer-driven polling — emits HealthReport on change.
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .Subscribe(report => Console.WriteLine(report.Nodes.Count));

// Push-triggered — reacts to StatusChanged, no polling delay.
graph.ObserveHealthReport()
    .Subscribe(report => Console.WriteLine(report.Nodes.Count));

// Diff-based change stream — composable with any report source.
graph.PollHealthReport(TimeSpan.FromSeconds(30))
    .SelectHealthChanges()
    .Subscribe(change =>
        Console.WriteLine($"{change.Name}: {change.Previous} → {change.Current}"));
```

### Sharing streams across subscribers

The Rx helpers produce cold observables — each subscription runs its own pipeline. To share a single evaluation across multiple subscribers, use standard Rx multicast operators directly: `Publish().RefCount()` or `Replay(1).RefCount()`.

## Serialization

Both enums use `[JsonStringEnumConverter]` so they serialize as `"Healthy"` / `"Degraded"` / etc. The `HealthReport` and `HealthSnapshot` records are designed as wire-friendly DTOs:

```json
{
  "Nodes": [
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
| `HealthNode.cs` | Sealed class — `Name`, `Dependencies`, `Parents`, `DependsOn()`, `RemoveDependency()`, `Refresh()`, `ReportStatus()`, factory methods (`CreateDelegate`, `CreateComposite`) |
| `HealthStatus.cs` | `Healthy` → `Unknown` → `Degraded` → `Unhealthy` enum |
| `HealthEvaluation.cs` | Status + optional reason pair, with implicit conversion from `HealthStatus` |
| `Importance.cs` | `Required`, `Important`, `Optional`, `Resilient` enum |
| `HealthDependency.cs` | Record linking a `HealthNode` with its importance |
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
| `ServiceCollectionExtensions.cs` | `AddPrognosis` entry point — service node registration and graph materialization |
| `PrognosisBuilder.cs` | Fluent builder — `AddServiceNode<T>`, `AddDelegate<T>`, `AddComposite`, `MarkAsRoot` |
| `DependencyConfigurator.cs` | Fluent edge declaration — `DependsOn<T>` (by type name), `DependsOn(name)` |
| `DependsOnAttribute.cs` | `[DependsOn("name", Importance)]` property-level attribute for declarative edges |
| `HealthGraph.cs` | Type forwarder for core `HealthGraph` (`Root`, indexer, `GetReport()`) |
| `HealthGraphOfT.cs` | `HealthGraph<TRoot>` typed wrapper for multi-root DI resolution |
| `PrognosisMonitorExtensions.cs` | `UseMonitor` extension + `IHostedService` adapter |

## Source generators and analyzers

The `Prognosis.Generators` package provides compile-time tooling for health graph development. Add it as an analyzer reference:

```xml
<ProjectReference Include="path\to\Prognosis.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

### Auto-generated `HealthNames` constants

The generator scans every `HealthNode.CreateDelegate("name")` and `HealthNode.CreateComposite("name")` call in your project and emits a `HealthNames` class with `const string` fields:

```csharp
// You write:
var db = HealthNode.CreateDelegate("Database.Connection", () => ...);
var cache = HealthNode.CreateDelegate("Cache", () => ...);
var app = HealthNode.CreateComposite("Application");

// Generator emits (HealthNames.g.cs):
public static class HealthNames
{
    public const string Application = "Application";
    public const string Cache = "Cache";
    public const string Database_Connection = "Database.Connection";
}
```

Use the generated constants at reference sites for autocomplete, find-all-references, and rename safety:

```csharp
// Instead of:
report.Nodes.First(n => n.Name == "Database.Connection");

// Use:
report.Nodes.First(n => n.Name == HealthNames.Database_Connection);
```

### PROGNOSIS001 — unknown node name

The `DependsOnEdgeAnalyzer` validates string arguments in `DependencyConfigurator.DependsOn("name", ...)` calls against the discovered node names. A typo produces a compile-time warning:

```
warning PROGNOSIS001: Node name 'Databse' does not match any
HealthNode.CreateDelegate or HealthNode.CreateComposite call in this compilation
```

### Auto-generated `AddDiscoveredNodes()`

When your project references both `Prognosis.DependencyInjection` and `Prognosis.Generators`, the `ServiceNodeDiscoveryGenerator` scans for classes with public `HealthNode` properties and emits an `AddDiscoveredNodes()` extension method on `PrognosisBuilder`:

```csharp
// You write:
class AuthService
{
    [DependsOn("Database", Importance.Required)]
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("AuthService");
}

// Generator emits:
builder.AddServiceNode<AuthService>(svc => svc.HealthNode, deps =>
{
    deps.DependsOn("Database", Importance.Required);
});
```

This replaces the previous reflection-based `ScanForServices()` pattern with zero-reflection, compile-time discovery.

> **Scope:** The generators and analyzer operate within a single compilation. Nodes created at runtime by the DI builder (e.g., `AddDelegate`, `AddComposite`) are not visible to the generator. Use hand-written `const` fields for those names.

## Requirements

- .NET Standard 2.0 or .NET Standard 2.1 compatible runtime (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
- [System.Text.Json](https://www.nuget.org/packages/System.Text.Json) (bundled as a dependency)
- [Microsoft.Bcl.AsyncInterfaces](https://www.nuget.org/packages/Microsoft.Bcl.AsyncInterfaces) (netstandard2.0 only, bundled as a dependency)
