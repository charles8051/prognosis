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

    health.UseMonitor(TimeSpan.FromSeconds(30));
});
```

## API

### Assembly scanning

`ScanForServices` discovers all concrete `IHealthAware` implementations in the given assemblies and registers them as singletons. It also reads `[DependsOn<T>]` attributes to auto-wire dependency edges:

```csharp
[DependsOn<DatabaseService>(Importance.Required)]
[DependsOn<CacheService>(Importance.Important)]
class AuthService : IObservableHealthNode
{
    private readonly HealthTracker _health = new();

    public string Name => "AuthService";
    public IReadOnlyList<HealthDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthEvaluation Evaluate() => _health.Evaluate();
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
        : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP refused"));
```

### Roots

Mark named services as top-level graph entry points for monitoring and report generation:

```csharp
health.AddRoots("Application");
```

### `HealthGraph`

The materialized graph is registered as a singleton. Inject it to access the roots, look up services by name, or create reports:

```csharp
var graph = serviceProvider.GetRequiredService<HealthGraph>();

// Create a point-in-time report.
HealthReport report = graph.CreateReport();

// Look up a service by name.
IHealthAware db = graph["Database"];

// Enumerate all services.
foreach (var svc in graph.Services)
{
    Console.WriteLine($"{svc.Name}: {svc.Evaluate()}");
}
```

The `Roots` property is `IHealthAware[]`, directly compatible with the Rx extensions in `Prognosis.Reactive`:

```csharp
graph.Roots.PollHealthReport(TimeSpan.FromSeconds(30)).Subscribe(...);
```

### Hosted monitoring

`UseMonitor` registers `HealthMonitor` as an `IHostedService` that polls on the given interval and stops with the host:

```csharp
health.UseMonitor(TimeSpan.FromSeconds(30));
```

This is optional — Rx users can skip it and build their own pipeline from `HealthGraph.Roots`.

## Dependencies

- [Prognosis](https://www.nuget.org/packages/Prognosis) (core library)
- [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions) >= 9.0.0
- [Microsoft.Extensions.Hosting.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.Abstractions) >= 9.0.0

## Requirements

- .NET Standard 2.0+ (.NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+)
