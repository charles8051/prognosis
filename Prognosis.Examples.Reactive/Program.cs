using System.Reactive.Linq;
using Prognosis;
using Prognosis.Reactive;

// ─────────────────────────────────────────────────────────────────────
// Build a small health graph manually (same as core example).
// ─────────────────────────────────────────────────────────────────────

var database = new DatabaseService();
var cache = new CacheService();
var externalEmailApi = new ThirdPartyEmailClient();

var emailHealth = new DelegatingServiceHealth("EmailProvider",
    () => externalEmailApi.IsConnected
        ? HealthStatus.Healthy
        : new HealthEvaluation(HealthStatus.Unhealthy, "SMTP connection refused"));

var messageQueue = new DelegatingServiceHealth("MessageQueue");

var authService = new DelegatingServiceHealth("AuthService")
    .DependsOn(database.Health, ServiceImportance.Required)
    .DependsOn(cache.Health, ServiceImportance.Important);

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

var roots = new ServiceHealth[] { app };

// ─────────────────────────────────────────────────────────────────────
// PollHealthReport — timer-driven, emits HealthReport on change.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== PollHealthReport (polling every 1 second) ===");
Console.WriteLine();

using var pollSubscription = roots
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
// ObserveHealthReport — push-triggered via leaf StatusChanged streams.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== ObserveHealthReport (push-triggered, 500ms throttle) ===");
Console.WriteLine();

using var observeSubscription = roots
    .ObserveHealthReport(TimeSpan.FromMilliseconds(500))
    .Subscribe(report =>
        Console.WriteLine($"  [Observe] Overall={report.OverallStatus} " +
            $"({report.Services.Count} services @ {report.Timestamp:HH:mm:ss.fff})"));

// Trigger a change — the leaf's StatusChanged fires immediately,
// throttle elapses, then a single-pass evaluation runs.
Console.WriteLine("  Taking cache offline...");
cache.IsConnected = false;
cache.Health.NotifyChanged(); // push the change
await Task.Delay(TimeSpan.FromSeconds(1));

Console.WriteLine("  Restoring cache...");
cache.IsConnected = true;
cache.Health.NotifyChanged();
await Task.Delay(TimeSpan.FromSeconds(1));
Console.WriteLine();

observeSubscription.Dispose();

// ─────────────────────────────────────────────────────────────────────
// SelectServiceChanges — diffs consecutive reports into individual
// ServiceStatusChange events.
// ─────────────────────────────────────────────────────────────────────

Console.WriteLine("=== SelectServiceChanges (diff-based change stream) ===");
Console.WriteLine();

using var changeSubscription = roots
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

class DatabaseService : IServiceHealth
{
    public ServiceHealth Health { get; }

    public DatabaseService()
    {
        Health = new DelegatingServiceHealth("Database",
            () => IsConnected
                ? HealthStatus.Healthy
                : new HealthEvaluation(HealthStatus.Unhealthy, "Connection lost"));
    }

    public bool IsConnected { get; set; } = true;
}

class CacheService : IServiceHealth
{
    public ServiceHealth Health { get; }

    public CacheService()
    {
        Health = new DelegatingServiceHealth("Cache",
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
