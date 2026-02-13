using ServiceHealthModel;

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
    Console.WriteLine($"  {database}");
    Console.WriteLine($"  {cache}");
    Console.WriteLine($"  {messageQueue}");
    Console.WriteLine($"  {emailHealth}");
    Console.WriteLine($"  {authService}");
    Console.WriteLine($"  {notificationSystem}");
    Console.WriteLine($"  {app}");
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

// ─────────────────────────────────────────────────────────────────────
// Example service classes
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A service you own — implements <see cref="IServiceHealth"/> directly by
/// embedding a <see cref="ServiceHealthTracker"/>.
/// </summary>
class DatabaseService : IServiceHealth
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
    public HealthStatus Evaluate() => _health.Evaluate();
    public override string ToString() => $"{Name}: {Evaluate()}";
}

/// <summary>Another service you own, same pattern.</summary>
class CacheService : IServiceHealth
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
