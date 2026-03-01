# Prognosis.DependencyInjection

Microsoft.Extensions.DependencyInjection integration for the [Prognosis](https://www.nuget.org/packages/Prognosis) service health graph. Provides assembly scanning, a fluent graph builder, and hosted service monitoring.

## Installation

```
dotnet add package Prognosis.DependencyInjection
```

## Quick start

```csharp
using Prognosis.DependencyInjection;

builder.Services.AddPrognosis(health =>
{
    health.ScanForServices(typeof(Program).Assembly);

    health.AddComposite("Application", app =>
    {
        app.DependsOn<AuthService>(Importance.Required);
        app.DependsOn("NotificationSystem", Importance.Important);
    });

    health.MarkAsRoot("Application");
    health.UseMonitor(TimeSpan.FromSeconds(30));
});
```

## API

### Assembly scanning

`ScanForServices` discovers all concrete `IHealthAware` implementations in the given assemblies and registers them as singletons. It also reads `[DependsOn<T>]` attributes to auto-wire dependency edges:

```csharp
[DependsOn<DatabaseService>(Importance.Required)]
[DependsOn<CacheService>(Importance.Important)]
class AuthService : IHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("AuthService");
}
```

### Composite nodes

Define virtual aggregation points whose health is derived entirely from their dependencies:

```csharp
health.AddComposite("NotificationSystem", n =>
{
    n.DependsOn("MessageQueue", Importance.Required);
    n.DependsOn("EmailProvider", Importance.Optional);
});
```

Dependencies can reference services by type (`DependsOn<T>`) or by name (`DependsOn("name")`).

### Delegate wrappers

Wrap a DI-registered service you don't own with a health-check delegate:

```csharp
health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
    client => client.IsConnected
        ? HealthStatus.Healthy
        : HealthEvaluation.Unhealthy("SMTP refused"));
```

### Roots

Designate the root of the health graph with `MarkAsRoot`. When only one root is declared (or auto-detected), a plain `HealthGraph` singleton is registered:

```csharp
health.MarkAsRoot("Application");       // by name
health.MarkAsRoot<ApplicationRoot>();   // by type — also registers HealthGraph<ApplicationRoot>
```

If `MarkAsRoot` is not called and exactly one node has no parent, it is chosen automatically. If there are zero or more than one candidate, an exception is thrown at graph materialization with guidance to call `MarkAsRoot`.

#### Multiple roots (shared nodes, separate graphs)

Call `MarkAsRoot` more than once to materialize several `HealthGraph` instances from a single shared node pool. Each graph walks a different root but the underlying `HealthNode` instances (and their health state) are shared:

```csharp
builder.Services.AddPrognosis(health =>
{
    health.ScanForServices(typeof(Program).Assembly);

    health.AddComposite("Ops", ops =>
    {
        ops.DependsOn<DatabaseService>(Importance.Required);
        ops.DependsOn<CacheService>(Importance.Required);
    });

    health.AddComposite("Customer", cust =>
    {
        cust.DependsOn<AuthService>(Importance.Required);
    });

    health.MarkAsRoot("Ops");
    health.MarkAsRoot("Customer");
});
```

With multiple roots:

- Each graph is registered as a **keyed** `HealthGraph` (keyed by the root name).
- Use `MarkAsRoot<T>()` to also register a `HealthGraph<T>` for consumers without keyed service support.

```csharp
// Keyed resolution (Microsoft.Extensions.DependencyInjection 8+):
var opsGraph  = sp.GetRequiredKeyedService<HealthGraph>("Ops");
var custGraph = sp.GetRequiredKeyedService<HealthGraph>("Customer");

// Generic resolution (any DI container):
var opsGraph  = sp.GetRequiredService<HealthGraph<OpsRoot>>().Graph;
```

### `HealthGraph`

The materialized graph is registered as a singleton (single root) or as keyed singletons (multiple roots). Inject it to access the root, look up services by name, or create reports:

```csharp
var graph = serviceProvider.GetRequiredService<HealthGraph>();

// Create a point-in-time report.
HealthReport report = graph.CreateReport();

// Look up a service by name.
HealthNode db = graph["Database"];

// Enumerate all nodes reachable from the root.
foreach (var node in graph.Nodes)
{
    Console.WriteLine($"{node.Name}: {node}");
}
```

The Rx extensions in `Prognosis.Reactive` operate directly on `HealthGraph`:

```csharp
graph.PollHealthReport(TimeSpan.FromSeconds(30)).Subscribe(...);
```

### Hosted monitoring

`UseMonitor` registers `HealthMonitor` as an `IHostedService` that polls on the given interval and stops with the host:

```csharp
health.UseMonitor(TimeSpan.FromSeconds(30));
```

This is optional — Rx users can skip it and build their own pipeline from `HealthGraph`.

## Dependencies

- [Prognosis](https://www.nuget.org/packages/Prognosis) (core library)
- [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions) >= 9.0.0
- [Microsoft.Extensions.Hosting.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.Abstractions) >= 9.0.0

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
