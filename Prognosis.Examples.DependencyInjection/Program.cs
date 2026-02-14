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
    // Scan the assembly for all IServiceHealth implementations.
    // DatabaseService and CacheService are discovered automatically.
    // [DependsOn<T>] attributes on those classes are read and wired.
    health.ScanForServices(typeof(Program).Assembly);

    // Wrap a third-party service you don't own with a health delegate.
    health.AddDelegate<ThirdPartyEmailClient>("EmailProvider",
        client => client.IsConnected
            ? HealthStatus.Healthy
            : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));

    // Define composite aggregation nodes.
    health.AddComposite("NotificationSystem", n =>
    {
        n.DependsOn("MessageQueue", ServiceImportance.Required);
        n.DependsOn("EmailProvider", ServiceImportance.Optional);
    });

    health.AddComposite("Application", app =>
    {
        app.DependsOn<AuthService>(ServiceImportance.Required);
        app.DependsOn("NotificationSystem", ServiceImportance.Important);
    });

    // Mark the top-level node as a root for monitoring.
    health.AddRoots("Application");

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
HealthAggregator.NotifyGraph(graph.Roots);
var after = graph.CreateReport();

var changes = HealthAggregator.Diff(before, after);
foreach (var change in changes)
{
    Console.WriteLine($"  {change.Name}: {change.Previous} → {change.Current} ({change.Reason})");
}
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────
// Look up services by name.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthGraph lookup ===");
foreach (var svc in graph.Services)
{
    Console.WriteLine($"  {svc.Name}: {svc.Evaluate()}");
}
Console.WriteLine();

if (graph.TryGetService("AuthService", out var auth))
{
    Console.WriteLine($"  AuthService has {auth.Dependencies.Count} dependencies");
}

// ─────────────────────────────────────────────────────────────────────
// Example service classes
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A service you own — implements <see cref="IObservableServiceHealth"/> directly
/// by embedding a <see cref="ServiceHealthTracker"/>.
/// </summary>
class DatabaseService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health;

    public DatabaseService()
    {
        _health = new ServiceHealthTracker(
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));
    }

    public bool IsConnected { get; set; } = true;

    public string Name => "Database";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthEvaluation Evaluate() => _health.Evaluate();
}

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health;

    public CacheService()
    {
        _health = new ServiceHealthTracker(
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Redis timeout"));
    }

    public bool IsConnected { get; set; } = true;

    public string Name => "Cache";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthEvaluation Evaluate() => _health.Evaluate();
}

/// <summary>
/// A service that declares its dependencies via attributes.
/// The scanner reads these and wires the edges automatically.
/// Uses <see cref="ServiceHealthTracker"/> internally since
/// <see cref="DelegatingServiceHealth"/> is sealed.
/// </summary>
[DependsOn<DatabaseService>(ServiceImportance.Required)]
[DependsOn<CacheService>(ServiceImportance.Important)]
class AuthService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health = new();

    public string Name => "AuthService";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthEvaluation Evaluate() => _health.Evaluate();
}

/// <summary>Always-healthy placeholder for demo purposes.</summary>
class MessageQueueService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health = new(() => HealthStatus.Healthy);

    public string Name => "MessageQueue";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthEvaluation Evaluate() => _health.Evaluate();
}

/// <summary>
/// A third-party class you cannot modify — no <see cref="IServiceHealth"/> on it.
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
