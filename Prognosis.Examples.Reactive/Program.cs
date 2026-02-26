using System.Reactive.Linq;
using Prognosis;
using Prognosis.Reactive;

// ─────────────────────────────────────────────────────────────────────
// Build a small health graph manually (same as core example).
// ─────────────────────────────────────────────────────────────────────

var database = new DatabaseService();
var cache = new CacheService();
var externalEmailApi = new ThirdPartyEmailClient();

var emailHealth = new HealthAdapter("EmailProvider",
    () => externalEmailApi.IsConnected
        ? HealthStatus.Healthy
        : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));

var messageQueue = new HealthAdapter("MessageQueue");

var authService = new HealthAdapter("AuthService")
    .DependsOn(database.HealthNode, Importance.Required)
    .DependsOn(cache.HealthNode, Importance.Important);

var notificationSystem = new HealthGroup("NotificationSystem")
    .DependsOn(messageQueue, Importance.Required)
    .DependsOn(emailHealth, Importance.Optional);

var app = new HealthGroup("Application")
    .DependsOn(authService, Importance.Required)
    .DependsOn(notificationSystem, Importance.Important);

// Hand the graph any entry-point node — roots are discovered from the topology.
var graph = HealthGraph.Create(app);

// ─────────────────────────────────────────────────────────────────────
// HealthGraph.PollHealthReport — timer-driven, whole-graph polling.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthGraph.PollHealthReport (polling every 1 second) ===");
Console.WriteLine();

using var graphPollSub = graph
    .PollHealthReport(TimeSpan.FromSeconds(1))
    .Subscribe(report =>
        Console.WriteLine($"  [Graph Poll] Overall={report.OverallStatus} " +
            $"({report.Services.Count} services @ {report.Timestamp:HH:mm:ss.fff})"));

await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine("  Restoring database...");
database.IsConnected = true;
await Task.Delay(TimeSpan.FromSeconds(1.5));
Console.WriteLine();

graphPollSub.Dispose();

// ─────────────────────────────────────────────────────────────────────
// HealthGraph.ObserveHealthReport — push-triggered, whole-graph report.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthGraph.ObserveHealthReport (push-triggered) ===");
Console.WriteLine();

using var graphObserveSub = graph
    .ObserveHealthReport()
    .Subscribe(report =>
        Console.WriteLine($"  [Graph Observe] Overall={report.OverallStatus} " +
            $"({report.Services.Count} services @ {report.Timestamp:HH:mm:ss.fff})"));

Console.WriteLine("  Taking cache offline...");
cache.IsConnected = false;
cache.HealthNode.BubbleChange();
await Task.Delay(TimeSpan.FromSeconds(1));

Console.WriteLine("  Restoring cache...");
cache.IsConnected = true;
cache.HealthNode.BubbleChange();
await Task.Delay(TimeSpan.FromSeconds(1));
Console.WriteLine();

graphObserveSub.Dispose();

// ─────────────────────────────────────────────────────────────────────
// HealthGraph + SelectServiceChanges — diff-based change stream.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthGraph.PollHealthReport + SelectServiceChanges ===");
Console.WriteLine();

using var graphChangeSub = graph
    .PollHealthReport(TimeSpan.FromSeconds(1))
    .SelectServiceChanges()
    .Subscribe(change =>
        Console.WriteLine($"  [Graph Change] {change.Name}: {change.Previous} → {change.Current}" +
            (change.Reason is not null ? $" ({change.Reason})" : "")));

await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine("  Restoring database...");
database.IsConnected = true;
await Task.Delay(TimeSpan.FromSeconds(1.5));
Console.WriteLine();

graphChangeSub.Dispose();

// ─────────────────────────────────────────────────────────────────────
// HealthNode extensions — same API on a single subtree.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthNode.PollHealthReport (single subtree, polling every 1 second) ===");
Console.WriteLine();

using var pollSubscription = app
    .PollHealthReport(TimeSpan.FromSeconds(1))
    .Subscribe(report =>
        Console.WriteLine($"  [Poll] Overall={report.OverallStatus} " +
            $"({report.Services.Count} services @ {report.Timestamp:HH:mm:ss.fff})"));

// Wait for the first tick to establish baseline.
await Task.Delay(TimeSpan.FromSeconds(1.5));

// Simulate a failure — the next tick picks it up.
Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine("  Restoring database...");
database.IsConnected = true;
await Task.Delay(TimeSpan.FromSeconds(1.5));
Console.WriteLine();

pollSubscription.Dispose();

// ─────────────────────────────────────────────────────────────────────
// HealthNode.ObserveHealthReport — push-triggered on a single node.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthNode.ObserveHealthReport (push-triggered on node) ===");
Console.WriteLine();

using var observeSubscription = app
    .ObserveHealthReport()
    .Subscribe(report =>
        Console.WriteLine($"  [Observe] Overall={report.OverallStatus} " +
            $"({report.Services.Count} services @ {report.Timestamp:HH:mm:ss.fff})"));

// Trigger a change — the leaf's BubbleChange propagates to the root,
// which fires StatusChanged, and a report is emitted immediately.
Console.WriteLine("  Taking cache offline...");
cache.IsConnected = false;
cache.HealthNode.BubbleChange(); // push the change
await Task.Delay(TimeSpan.FromSeconds(1));

Console.WriteLine("  Restoring cache...");
cache.IsConnected = true;
cache.HealthNode.BubbleChange();
await Task.Delay(TimeSpan.FromSeconds(1));
Console.WriteLine();

observeSubscription.Dispose();

// ─────────────────────────────────────────────────────────────────────
// HealthNode.SelectServiceChanges — diffs consecutive reports into
// individual StatusChange events.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== HealthNode.PollHealthReport + SelectServiceChanges ===");
Console.WriteLine();

using var changeSubscription = app
    .PollHealthReport(TimeSpan.FromSeconds(1))
    .SelectServiceChanges()
    .Subscribe(change =>
        Console.WriteLine($"  [Change] {change.Name}: {change.Previous} → {change.Current}" +
            (change.Reason is not null ? $" ({change.Reason})" : "")));

// Wait for baseline.
await Task.Delay(TimeSpan.FromSeconds(1.5));

// Take database offline — should see Database + AuthService + Application change.
Console.WriteLine("  Taking database offline...");
database.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));

// Restore — should see them revert.
Console.WriteLine("  Restoring database...");
database.IsConnected = true;
await Task.Delay(TimeSpan.FromSeconds(1.5));

// Take email offline — optional dependency, only EmailProvider itself changes.
Console.WriteLine("  Taking email provider offline (optional — only EmailProvider changes)...");
externalEmailApi.IsConnected = false;
await Task.Delay(TimeSpan.FromSeconds(1.5));

Console.WriteLine();
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────
// Example service classes (self-contained, same as core example)
// ─────────────────────────────────────────────────────────────────────

class DatabaseService : IHealthAware
{
    public HealthNode HealthNode { get; }

    public DatabaseService()
    {
        HealthNode = new HealthAdapter("Database",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));
    }

    public bool IsConnected { get; set; } = true;
}

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

class ThirdPartyEmailClient
{
    public bool IsConnected { get; set; } = true;
}
