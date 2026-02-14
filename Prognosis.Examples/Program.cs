using System.Text.Json;
using Prognosis;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

// ─────────────────────────────────────────────────────────────────────
// Pattern 1 — Implement IServiceHealth on a class you own.
//             Embed a ServiceHealthTracker and delegate to it.
// ─────────────────────────────────────────────────────────────────────
var database = new DatabaseService();
var cache = new CacheService();

// ─────────────────────────────────────────────────────────────────────
// Pattern 2 — Wrap a service you don't own (or don't want to modify)
//             with DelegatingServiceHealth and a health-check delegate.
// ─────────────────────────────────────────────────────────────────────
var externalEmailApi = new ThirdPartyEmailClient();        // some closed class
var emailHealth = new DelegatingServiceHealth("EmailProvider",
    () => externalEmailApi.IsConnected ? HealthStatus.Healthy : HealthStatus.Unhealthy);

var messageQueue = new DelegatingServiceHealth("MessageQueue"); // always healthy for demo

// ─────────────────────────────────────────────────────────────────────
// Pattern 3 — Pure composite aggregation (no backing service).
// ─────────────────────────────────────────────────────────────────────
var authService = new DelegatingServiceHealth("AuthService")
    .DependsOn(database, ServiceImportance.Required)
    .DependsOn(cache, ServiceImportance.Important);

var notificationSystem = new CompositeServiceHealth("NotificationSystem",
[
    new ServiceDependency(messageQueue, ServiceImportance.Required),
    new ServiceDependency(emailHealth, ServiceImportance.Optional),
]);

var app = new CompositeServiceHealth("Application",
[
    new ServiceDependency(authService, ServiceImportance.Required),
    new ServiceDependency(notificationSystem, ServiceImportance.Important),
]);

// ── Demo ─────────────────────────────────────────────────────────────
void PrintHealth()
{
    foreach (var snapshot in HealthAggregator.EvaluateAll(app))
    {
        Console.WriteLine($"  {snapshot}");
    }

    Console.WriteLine();
}

Console.WriteLine("=== All services healthy ===");
PrintHealth();

Console.WriteLine("=== Cache goes unhealthy (Important to AuthService → degrades it) ===");
cache.IsConnected = false;
PrintHealth();

Console.WriteLine("=== Database goes unhealthy (Required by AuthService → unhealthy cascades up) ===");
database.IsConnected = false;
PrintHealth();

Console.WriteLine("=== Only EmailProvider unhealthy (Optional to NotificationSystem → no effect) ===");
database.IsConnected = true;
cache.IsConnected = true;
externalEmailApi.IsConnected = false;
PrintHealth();

// ── Cycle detection ──────────────────────────────────────────────────
Console.WriteLine("=== Upfront cycle detection ===");
var cycles = HealthAggregator.DetectCycles(app);
Console.WriteLine(cycles.Count == 0
    ? "  No cycles detected."
    : string.Join(Environment.NewLine, cycles.Select(c => "  Cycle: " + string.Join(" → ", c))));
Console.WriteLine();

// Now introduce a deliberate cycle and detect it.
var serviceA = new DelegatingServiceHealth("ServiceA");
var serviceB = new DelegatingServiceHealth("ServiceB")
    .DependsOn(serviceA, ServiceImportance.Required);
serviceA.DependsOn(serviceB, ServiceImportance.Required); // A → B → A

Console.WriteLine("=== After introducing ServiceA ↔ ServiceB cycle ===");
cycles = HealthAggregator.DetectCycles(serviceA);
Console.WriteLine(string.Join(Environment.NewLine, cycles.Select(c => "  Cycle: " + string.Join(" → ", c))));
Console.WriteLine();

// Evaluation still works — the re-entrancy guard prevents a stack overflow.
Console.WriteLine($"  ServiceA evaluates safely: {serviceA.Evaluate()}");
Console.WriteLine($"  ServiceB evaluates safely: {serviceB.Evaluate()}");
Console.WriteLine();

// ── Serialization ────────────────────────────────────────────────────
Console.WriteLine("=== Serialized health report (JSON) ===");
externalEmailApi.IsConnected = true; // reset for clean report
var report = HealthAggregator.CreateReport(app);
Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
Console.WriteLine();

// ── Observable health monitoring ─────────────────────────────────────
Console.WriteLine("=== Observable health monitoring ===");

// Reset all services to healthy.
database.IsConnected = true;
cache.IsConnected = true;
externalEmailApi.IsConnected = true;

// Subscribe to an individual service's status changes.
using var dbSubscription = database.StatusChanged.Subscribe(
    new StatusObserver("Database"));

// Subscribe to graph-level report changes via HealthMonitor.
await using var monitor = new HealthMonitor([app], TimeSpan.FromSeconds(1));
using var reportSubscription = monitor.ReportChanged.Subscribe(
    new ReportObserver());

// Initial poll to establish baseline.
Console.WriteLine("  Polling initial state...");
monitor.Poll();
Console.WriteLine();

// Simulate a change — detected on manual poll.
Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
monitor.Poll();
Console.WriteLine();

// Bring it back.
Console.WriteLine("  Restoring database...");
database.IsConnected = true;
monitor.Poll();
Console.WriteLine();

// Let the timer do the work — change state, then wait for a tick.
Console.WriteLine("  Taking cache offline and waiting for next timer tick...");
cache.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────
// Example service classes
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A service you own — implements <see cref="IObservableServiceHealth"/> directly by
/// embedding a <see cref="ServiceHealthTracker"/>.
/// </summary>
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
    public override string ToString() => $"{Name}: {Evaluate()}";
}

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IObservableServiceHealth
{
    private readonly ServiceHealthTracker _health;

    public CacheService()
    {
        _health = new ServiceHealthTracker(
            () => IsConnected ? HealthStatus.Healthy : HealthStatus.Unhealthy);
    }

    public bool IsConnected { get; set; } = true;

    public string Name => "Cache";
    public IReadOnlyList<ServiceDependency> Dependencies => _health.Dependencies;
    public IObservable<HealthStatus> StatusChanged => _health.StatusChanged;
    public void NotifyChanged() => _health.NotifyChanged();
    public HealthStatus Evaluate() => _health.Evaluate();
    public override string ToString() => $"{Name}: {Evaluate()}";
}

/// <summary>
/// A third-party class you cannot modify — no <see cref="IServiceHealth"/> on it.
/// Wrapped via <see cref="DelegatingServiceHealth"/> above.
/// </summary>
class ThirdPartyEmailClient
{
    public bool IsConnected { get; set; } = true;
}

// ─────────────────────────────────────────────────────────────────────
// Minimal IObserver<T> implementations for the demo
// ─────────────────────────────────────────────────────────────────────

class StatusObserver(string name) : IObserver<HealthStatus>
{
    public void OnNext(HealthStatus value) =>
        Console.WriteLine($"    >> {name} status changed: {value}");
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}

class ReportObserver : IObserver<HealthReport>
{
    public void OnNext(HealthReport value) =>
        Console.WriteLine($"    >> Report changed: Overall={value.OverallStatus} " +
            $"({value.Services.Count} services @ {value.Timestamp:HH:mm:ss.fff})");
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
