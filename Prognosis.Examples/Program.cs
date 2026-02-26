using System.Text.Json;
using Prognosis;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

// ─────────────────────────────────────────────────────────────────────
// Pattern 1 — Implement IHealthAware on a class you own.
//             Embed a HealthNode property.
//             DatabaseService uses a HealthGroup backed by
//             fine-grained sub-nodes (connection, latency, pool).
// ─────────────────────────────────────────────────────────────────────
var database = new DatabaseService();
var cache = new CacheService();

// ─────────────────────────────────────────────────────────────────────
// Pattern 2 — Wrap a service you don't own (or don't want to modify)
//             with HealthCheck and a health-check delegate.
// ─────────────────────────────────────────────────────────────────────
var externalEmailApi = new ThirdPartyEmailClient();        // some closed class
var emailHealth = new HealthCheck("EmailProvider",
    () => externalEmailApi.IsConnected
        ? HealthStatus.Healthy
        : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));

var messageQueue = new HealthCheck("MessageQueue"); // always healthy for demo

// ─────────────────────────────────────────────────────────────────────
// Pattern 3 — Pure composite aggregation (no backing service).
//             The fluent .DependsOn() API reads naturally and avoids
//             having to wrap every edge in a HealthDependency object.
// ─────────────────────────────────────────────────────────────────────
var authService = new HealthCheck("AuthService")
    .DependsOn(database.Health, Importance.Required)
    .DependsOn(cache.Health, Importance.Important);

var notificationSystem = new HealthGroup("NotificationSystem")
    .DependsOn(messageQueue, Importance.Required)
    .DependsOn(emailHealth, Importance.Optional);

var app = new HealthGroup("Application")
    .DependsOn(authService, Importance.Required)
    .DependsOn(notificationSystem, Importance.Important);

// ─────────────────────────────────────────────────────────────────────
// Pattern 4 — HealthGraph: hand the topology root node(s) and the
//             graph discovers every reachable dependency downward.
// ─────────────────────────────────────────────────────────────────────
var graph = HealthGraph.Create(app);

// ── Demo ─────────────────────────────────────────────────────────────
void PrintHealth()
{
    foreach (var snapshot in graph.EvaluateAll())
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

// ── Fine-grained database health ─────────────────────────────────────
Console.WriteLine("=== Database sub-graph: high latency (Important → degrades Database) ===");
externalEmailApi.IsConnected = true;
database.AverageLatencyMs = 600;   // above the 500ms threshold
PrintHealth();

Console.WriteLine("=== Database sub-graph: connection pool exhausted (Required → unhealthy) ===");
database.AverageLatencyMs = 50;    // restore latency
database.PoolUtilization = 1.0;   // 100% — exhausted
PrintHealth();

Console.WriteLine("=== Database sub-graph: everything restored ===");
database.PoolUtilization = 0.3;
PrintHealth();

// ── Cycle detection ──────────────────────────────────────────────────
Console.WriteLine("=== Upfront cycle detection ===");
var cycles = graph.DetectCycles();
Console.WriteLine(cycles.Count == 0
    ? "  No cycles detected."
    : string.Join(Environment.NewLine, cycles.Select(c => "  Cycle: " + string.Join(" → ", c))));
Console.WriteLine();

// Now introduce a deliberate cycle and detect it.
var nodeA = new HealthCheck("ServiceA");
var nodeB = new HealthCheck("ServiceB")
    .DependsOn(nodeA, Importance.Required);
nodeA.DependsOn(nodeB, Importance.Required); // A → B → A

Console.WriteLine("=== After introducing ServiceA ↔ ServiceB cycle ===");
var cycleGraph = HealthGraph.Create(nodeA);
cycles = cycleGraph.DetectCycles();
Console.WriteLine(string.Join(Environment.NewLine, cycles.Select(c => "  Cycle: " + string.Join(" → ", c))));
Console.WriteLine();

// Evaluation still works — the re-entrancy guard prevents a stack overflow.
Console.WriteLine($"  ServiceA evaluates safely: {nodeA.Evaluate()}");
Console.WriteLine($"  ServiceB evaluates safely: {nodeB.Evaluate()}");
Console.WriteLine();

// ── Serialization ────────────────────────────────────────────────────
Console.WriteLine("=== Serialized health report (JSON) ===");
externalEmailApi.IsConnected = true; // reset for clean report
var report = graph.CreateReport();
Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
Console.WriteLine();

// ── Observable health monitoring ─────────────────────────────────────
Console.WriteLine("=== Observable health monitoring ===");

// Reset all services to healthy.
database.IsConnected = true;
cache.IsConnected = true;
externalEmailApi.IsConnected = true;

// Subscribe to an individual service's status changes.
using var dbSubscription = database.Health.StatusChanged.Subscribe(
    new StatusObserver("Database"));

// Subscribe to graph-level report changes via HealthMonitor.
// The HealthGraph overload re-queries Roots each tick, so runtime
// edge changes are reflected automatically.
await using var monitor = new HealthMonitor(graph, TimeSpan.FromSeconds(1));
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
/// A service you own — implement <see cref="IHealthAware"/> and expose a
/// <see cref="HealthNode"/> property. Here the top-level node is a
/// <see cref="HealthGroup"/> whose health is derived entirely from
/// three fine-grained <see cref="HealthCheck"/> sub-nodes:
/// connection, latency, and connection-pool utilization.
/// </summary>
class DatabaseService : IHealthAware
{
    public HealthNode Health { get; }

    public bool IsConnected { get; set; } = true;
    public double AverageLatencyMs { get; set; } = 50;
    public double PoolUtilization { get; set; } = 0.3;

    public DatabaseService()
    {
        var connection = new HealthCheck("Database.Connection",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));

        var latency = new HealthCheck("Database.Latency",
            () => AverageLatencyMs switch
            {
                > 500 => new HealthEvaluation(HealthStatus.Degraded,
                    $"Avg latency {AverageLatencyMs:F0}ms exceeds 500ms threshold"),
                _ => HealthStatus.Healthy,
            });

        var connectionPool = new HealthCheck("Database.ConnectionPool",
            () => PoolUtilization switch
            {
                >= 1.0 => new HealthEvaluation(HealthStatus.Unhealthy, "Connection pool exhausted"),
                >= 0.9 => new HealthEvaluation(HealthStatus.Degraded,
                    $"Connection pool at {PoolUtilization:P0} utilization"),
                _ => HealthStatus.Healthy,
            });

        Health = new HealthGroup("Database")
            .DependsOn(connection, Importance.Required)
            .DependsOn(latency, Importance.Important)
            .DependsOn(connectionPool, Importance.Required);
    }
}

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IHealthAware
{
    public HealthNode Health { get; }

    public CacheService()
    {
        Health = new HealthCheck("Cache",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Redis timeout"));
    }

    public bool IsConnected { get; set; } = true;
}

/// <summary>
/// A third-party class you cannot modify — no <see cref="IHealthAware"/> on it.
/// Wrapped via <see cref="HealthCheck"/> above.
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
