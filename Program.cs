using ServiceHealthModel;

// ── Leaf services (real backing services) ───────────────────────────
var database = new LeafServiceHealth("Database");
var cache = new LeafServiceHealth("Cache");
var messageQueue = new LeafServiceHealth("MessageQueue");
var emailProvider = new LeafServiceHealth("EmailProvider");

// ── A leaf service that itself depends on the database ──────────────
var authService = new LeafServiceHealth("AuthService", dependencies:
[
    new ServiceDependency(database, ServiceImportance.Required),
    new ServiceDependency(cache, ServiceImportance.Important),
]);

// ── A pure composite — no real service behind it ────────────────────
var notificationSystem = new CompositeServiceHealth("NotificationSystem",
[
    new ServiceDependency(messageQueue, ServiceImportance.Required),
    new ServiceDependency(emailProvider, ServiceImportance.Optional),
]);

// ── Top-level application health (also a composite) ─────────────────
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
    Console.WriteLine($"  {emailProvider}");
    Console.WriteLine($"  {authService}");
    Console.WriteLine($"  {notificationSystem}");
    Console.WriteLine($"  {app}");
    Console.WriteLine();
}

Console.WriteLine("=== All services healthy ===");
PrintHealth();

Console.WriteLine("=== Cache goes unhealthy (Important to AuthService → degrades it) ===");
cache.IntrinsicStatus = HealthStatus.Unhealthy;
PrintHealth();

Console.WriteLine("=== Database goes unhealthy (Required by AuthService → unhealthy cascades up) ===");
database.IntrinsicStatus = HealthStatus.Unhealthy;
PrintHealth();

Console.WriteLine("=== Only EmailProvider unhealthy (Optional to NotificationSystem → no effect) ===");
database.IntrinsicStatus = HealthStatus.Healthy;
cache.IntrinsicStatus = HealthStatus.Healthy;
emailProvider.IntrinsicStatus = HealthStatus.Unhealthy;
PrintHealth();
