using System.Text.Json;
using Prognosis;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

// ─────────────────────────────────────────────────────────────────────
// Pattern 1 — Implement IHealthAware on a class you own.
//             Embed a HealthNode property.
//             DatabaseService uses a composite HealthNode backed by
//             fine-grained sub-nodes (connection, latency, pool).
// ─────────────────────────────────────────────────────────────────────
var database = new DatabaseService();
var cache = new CacheService();

// ─────────────────────────────────────────────────────────────────────
// Pattern 2 — Wrap a service you don't own (or don't want to modify)
//             with HealthNode.CreateDelegate and a health-check delegate.
// ─────────────────────────────────────────────────────────────────────
var externalEmailApi = new ThirdPartyEmailClient();        // some closed class
var emailHealth = HealthNode.CreateDelegate("EmailProvider",
    () => externalEmailApi.IsConnected
        ? HealthStatus.Healthy
        : HealthEvaluation.Unhealthy("SMTP connection refused"));

var messageQueue = HealthNode.CreateDelegate("MessageQueue"); // always healthy for demo

// ─────────────────────────────────────────────────────────────────────
// Pattern 3 — Pure composite aggregation (no backing service).
//             The fluent .DependsOn() API reads naturally and avoids
//             having to wrap every edge in a HealthDependency object.
// ─────────────────────────────────────────────────────────────────────
var authService = HealthNode.CreateDelegate("AuthService")
    .DependsOn(database.HealthNode, Importance.Required)
    .DependsOn(cache.HealthNode, Importance.Important);

var notificationSystem = HealthNode.CreateComposite("NotificationSystem")
    .DependsOn(messageQueue, Importance.Required)
    .DependsOn(emailHealth, Importance.Optional);

var app = HealthNode.CreateComposite("Application")
    .DependsOn(authService, Importance.Required)
    .DependsOn(notificationSystem, Importance.Important);

// ─────────────────────────────────────────────────────────────────────
// Pattern 4 — HealthGraph: hand the topology a single root node and the
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
var nodeA = HealthNode.CreateDelegate("ServiceA");
var nodeB = HealthNode.CreateDelegate("ServiceB")
    .DependsOn(nodeA, Importance.Required);
nodeA.DependsOn(nodeB, Importance.Required); // A → B → A

Console.WriteLine("=== After introducing ServiceA ↔ ServiceB cycle ===");
var cycleGraph = HealthGraph.Create(nodeA);
cycles = cycleGraph.DetectCycles();
Console.WriteLine(string.Join(Environment.NewLine, cycles.Select(c => "  Cycle: " + string.Join(" → ", c))));
Console.WriteLine();

// Evaluation still works — the re-entrancy guard prevents a stack overflow.
Console.WriteLine($"  ServiceA evaluates safely: {cycleGraph.Evaluate("ServiceA")}");
Console.WriteLine($"  ServiceB evaluates safely: {cycleGraph.Evaluate("ServiceB")}");
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

// Subscribe to graph-level status changes.
using var statusSubscription = graph.StatusChanged.Subscribe(
    new ReportObserver());

// Subscribe to graph-level report changes via HealthMonitor.
// The HealthGraph overload re-queries Roots each tick, so runtime
// edge changes are reflected automatically.
await using var monitor = new HealthMonitor(graph, TimeSpan.FromSeconds(1));
monitor.Start();
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
/// <see cref="HealthNode"/> property. Here the top-level node is created via
/// <see cref="HealthNode.CreateComposite"/> whose health is derived entirely
/// from three fine-grained sub-nodes created via
/// <see cref="HealthNode.CreateDelegate(string, Func{HealthEvaluation})"/>:
/// connection, latency, and connection-pool utilization.
/// </summary>
class DatabaseService : IHealthAware
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
                >= 1.0 => HealthEvaluation.Unhealthy("Connection pool exhausted"),
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

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IHealthAware
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

/// <summary>
/// A third-party class you cannot modify — no <see cref="IHealthAware"/> on it.
/// Wrapped via <see cref="HealthNode.CreateDelegate(string, Func{HealthEvaluation})"/> above.
/// </summary>
class ThirdPartyEmailClient
{
    public bool IsConnected { get; set; } = true;
}

// ─────────────────────────────────────────────────────────────────────
// Minimal IObserver<T> implementation for the demo
// ─────────────────────────────────────────────────────────────────────

class ReportObserver : IObserver<HealthReport>
{
    public void OnNext(HealthReport value) =>
        Console.WriteLine($"    >> Report changed: {value.Nodes.Count} nodes");
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
