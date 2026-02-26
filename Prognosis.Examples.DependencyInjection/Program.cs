using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prognosis;
using Prognosis.DependencyInjection;

// ─────────────────────────────────────────────────────────────────────
// Build the host with Prognosis health graph wired via DI.
// ─────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// Register the third-party service we'll wrap with a health delegate.
builder.Services.AddSingleton<ThirdPartyEmailClient>();

builder.Services.AddPrognosis(health =>
{
    // Scan the assembly for all IHealthAware implementations.
    // DatabaseService and CacheService are discovered automatically.
    // [DependsOn<T>] attributes on those classes are read and wired.
    health.ScanForServices(typeof(Program).Assembly);

    // Wrap a third-party service you don't own with a health delegate.
    // Name defaults to typeof(ThirdPartyEmailClient).Name when omitted.
    health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
        client => client.IsConnected
            ? HealthStatus.Healthy
            : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));

    // Define composite aggregation nodes.
    // Use constants or nameof to avoid magic strings — refactoring-safe.
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

    // Designate the root — required when multiple top-level nodes exist.
    health.MarkAsRoot(ServiceNames.Application);

    // Register HealthMonitor as a hosted service (polls every 2 seconds).
    health.UseMonitor(TimeSpan.FromSeconds(2));
});

var host = builder.Build();

// ─────────────────────────────────────────────────────────────────────
// Resolve the health graph and inspect it.
// ─────────────────────────────────────────────────────────────────────

var graph = host.Services.GetRequiredService<HealthGraph>();
var monitor = host.Services.GetRequiredService<HealthMonitor>();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

Console.WriteLine("=== Initial health report ===");
var report = graph.CreateReport();
Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────
// Subscribe to report changes and simulate failures.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== Subscribing to HealthMonitor.ReportChanged ===");
using var subscription = monitor.ReportChanged.Subscribe(new ReportObserver());

// Initial poll to establish baseline.
monitor.Poll();
Console.WriteLine();

// Simulate a database failure.
var database = host.Services.GetRequiredService<DatabaseService>();
Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
monitor.Poll();
Console.WriteLine();

// Restore it.
Console.WriteLine("  Restoring database...");
database.IsConnected = true;
monitor.Poll();
Console.WriteLine();

// Simulate an email provider failure (optional dependency — no effect on Application).
var emailClient = host.Services.GetRequiredService<ThirdPartyEmailClient>();
Console.WriteLine("  Taking email provider offline (optional — no effect on Application)...");
emailClient.IsConnected = false;
monitor.Poll();
Console.WriteLine();

// Show the diff API.
Console.WriteLine("=== Report diffing ===");
var before = graph.CreateReport();
emailClient.IsConnected = true;
database.IsConnected = false;
graph.NotifyAll();
var after = graph.CreateReport();

var changes = before.DiffTo(after);
foreach (var change in changes)
{
    Console.WriteLine($"  {change.Name}: {change.Previous} → {change.Current} ({change.Reason})");
}
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────
// Look up services by name.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthGraph lookup ===");
foreach (var node in graph.Nodes)
{
    Console.WriteLine($"  {node.Name}: {node.Evaluate()}");
}
Console.WriteLine();

// Type-safe lookup — uses typeof(AuthService).Name as the key.
if (graph.TryGetNode<AuthService>(out var auth))
{
    Console.WriteLine($"  AuthService has {auth.Dependencies.Count} dependencies");
}

// ─────────────────────────────────────────────────────────────────────
// Multiple roots — shared nodes, separate graphs.
//
// A second AddPrognosis call creates a separate configuration with
// multiple MarkAsRoot calls. Each root produces a separate HealthGraph
// that shares the same underlying HealthNode instances.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("=== Multi-root health graphs ===");

var multiBuilder = Host.CreateApplicationBuilder(args);
multiBuilder.Services.AddSingleton<ThirdPartyEmailClient>();

multiBuilder.Services.AddPrognosis(health =>
{
    health.ScanForServices(typeof(Program).Assembly);

    health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
        client => client.IsConnected
            ? HealthStatus.Healthy
            : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP refused"));

    health.AddComposite(ServiceNames.NotificationSystem, n =>
    {
        n.DependsOn(nameof(MessageQueueService), Importance.Required);
        n.DependsOn("EmailProvider", Importance.Optional);
    });

    // Two different views of the same service pool.
    health.AddComposite(nameof(OpsView), ops =>
    {
        ops.DependsOn<DatabaseService>(Importance.Required);
        ops.DependsOn<CacheService>(Importance.Required);
        ops.DependsOn(ServiceNames.NotificationSystem, Importance.Important);
    });

    health.AddComposite(nameof(CustomerView), cust =>
    {
        cust.DependsOn<AuthService>(Importance.Required);
    });

    // Each call produces a separate HealthGraph. Because MarkAsRoot<T>()
    // is used, both keyed and generic resolutions are available.
    health.MarkAsRoot<OpsView>();
    health.MarkAsRoot<CustomerView>();
});

var multiHost = multiBuilder.Build();
var sp = multiHost.Services;

// Keyed resolution — use the root name as the key.
var opsGraphKeyed = sp.GetRequiredKeyedService<HealthGraph>(nameof(OpsView));
var custGraphKeyed = sp.GetRequiredKeyedService<HealthGraph>(nameof(CustomerView));

Console.WriteLine($"  Ops root (keyed):      {opsGraphKeyed.Root.Name} ({opsGraphKeyed.Nodes.Count()} nodes)");
Console.WriteLine($"  Customer root (keyed): {custGraphKeyed.Root.Name} ({custGraphKeyed.Nodes.Count()} nodes)");

// Generic resolution — works on any DI container without keyed support.
var opsGraphTyped = sp.GetRequiredService<HealthGraph<OpsView>>();
var custGraphTyped = sp.GetRequiredService<HealthGraph<CustomerView>>();

Console.WriteLine($"  Ops root (typed):      {opsGraphTyped.Root.Name}");
Console.WriteLine($"  Customer root (typed): {custGraphTyped.Root.Name}");

// Verify that nodes are shared across the two graphs.
opsGraphKeyed.TryGetNode("Database", out var opsDb);
custGraphKeyed.TryGetNode("Database", out var custDb);
Console.WriteLine($"  Shared Database node:  {ReferenceEquals(opsDb, custDb)}");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────
// Example service classes
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A service you own — implement <see cref="IHealthAware"/> and expose
/// a <see cref="DelegateHealthNode"/> property. No forwarding needed.
/// </summary>
class DatabaseService : IHealthAware
{
    public HealthNode HealthNode { get; }

    public DatabaseService()
    {
        HealthNode = new DelegateHealthNode("Database",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));
    }

    public bool IsConnected { get; set; } = true;
}

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IHealthAware
{
    public HealthNode HealthNode { get; }

    public CacheService()
    {
        HealthNode = new DelegateHealthNode("Cache",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Redis timeout"));
    }

    public bool IsConnected { get; set; } = true;
}

/// <summary>
/// A service that declares its dependencies via attributes.
/// The scanner reads these and wires the edges automatically.
/// </summary>
[DependsOn<DatabaseService>(Importance.Required)]
[DependsOn<CacheService>(Importance.Important)]
class AuthService : IHealthAware
{
    public HealthNode HealthNode { get; } = new DelegateHealthNode("AuthService");
}

/// <summary>Always-healthy placeholder for demo purposes.</summary>
class MessageQueueService : IHealthAware
{
    public HealthNode HealthNode { get; } = new DelegateHealthNode(nameof(MessageQueueService));
}

/// <summary>
/// A third-party class you cannot modify — no <see cref="IHealthAware"/> on it.
/// Wrapped via <c>AddDelegate&lt;T&gt;</c> in the builder above.
/// </summary>
class ThirdPartyEmailClient
{
    public bool IsConnected { get; set; } = true;
}

/// <summary>Minimal observer for the demo.</summary>
class ReportObserver : IObserver<HealthReport>
{
    public void OnNext(HealthReport value) =>
        Console.WriteLine($"    >> Report changed: Overall={value.OverallStatus} " +
            $"({value.Services.Count} services @ {value.Timestamp:HH:mm:ss.fff})");
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}

/// <summary>
/// Central constants for composite/virtual service names.
/// Eliminates magic strings across configuration, lookups, and assertions.
/// </summary>
static class ServiceNames
{
    public const string Application = nameof(Application);
    public const string NotificationSystem = nameof(NotificationSystem);
}

/// <summary>Marker types for multi-root graph resolution.</summary>
class OpsView;
class CustomerView;
