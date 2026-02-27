using Microsoft.Extensions.DependencyInjection;
using Prognosis;
using Prognosis.DependencyInjection;

namespace Prognosis.DependencyInjection.Tests;

public class ServiceCollectionExtensionsTests
{
    // ── Assembly scanning ────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_ScanForServices_RegistersIHealthAwareAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.AddComposite("Root", app =>
            {
                app.DependsOn<TestAuthService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Required);
            });
            health.MarkAsRoot("Root");
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode("TestDatabase", out _));
        Assert.True(graph.TryGetNode("TestCache", out _));
    }

    [Fact]
    public void AddPrognosis_ScanForServices_SameInstancePerResolution()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.MarkAsRoot<TestAuthService>();
        });

        var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<TestDatabaseService>();
        var b = sp.GetRequiredService<TestDatabaseService>();

        Assert.Same(a, b);
    }

    // ── [DependsOn<T>] attribute wiring ─────────────────────────────

    [Fact]
    public void AddPrognosis_DependsOnAttribute_WiresEdges()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.AddComposite("Root", app =>
            {
                app.DependsOn<TestAuthService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Required);
            });
            health.MarkAsRoot("Root");
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        // TestAuthService has [DependsOn<TestDatabaseService>(Required)]
        Assert.True(graph.TryGetNode("TestAuth", out var auth));
        Assert.Single(auth.Dependencies);
        Assert.Equal("TestDatabase", auth.Dependencies[0].Node.Name);
        Assert.Equal(Importance.Required, auth.Dependencies[0].Importance);
    }

    // ── AddDelegate ─────────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_AddDelegate_WrapsServiceWithHealthAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestExternalClient { IsUp = true });

        services.AddPrognosis(health =>
        {
            health.AddDelegate<TestExternalClient>("ExternalApi",
                client => client.IsUp
                    ? HealthStatus.Healthy
                    : HealthEvaluation.Unhealthy("down"));
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode("ExternalApi", out var node));
        Assert.Equal(HealthStatus.Healthy, graph.Evaluate("ExternalApi").Status);
    }

    [Fact]
    public void AddPrognosis_AddDelegate_DelegateReflectsServiceState()
    {
        var client = new TestExternalClient { IsUp = true };
        var services = new ServiceCollection();
        services.AddSingleton(client);

        services.AddPrognosis(health =>
        {
            health.AddDelegate<TestExternalClient>("ExternalApi",
                c => c.IsUp
                    ? HealthStatus.Healthy
                    : HealthEvaluation.Unhealthy("down"));
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        client.IsUp = false;

        Assert.True(graph.TryGetNode("ExternalApi", out var node));
        Assert.Equal(HealthStatus.Unhealthy, graph.Evaluate("ExternalApi").Status);
    }

    [Fact]
    public void AddPrognosis_AddDelegate_DefaultName_UsesTypeName()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestExternalClient { IsUp = true });

        services.AddPrognosis(health =>
        {
            health.AddDelegate<TestExternalClient>(
                client => HealthStatus.Healthy);
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode(nameof(TestExternalClient), out _));
    }

    // ── AddComposite ────────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_AddComposite_CreatesGroupNode()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite("Platform", app =>
            {
                app.DependsOn<TestDatabaseService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Important);
            });

            health.MarkAsRoot("Platform");
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode("Platform", out var platform));
        Assert.Equal(2, platform.Dependencies.Count);
        Assert.Equal(HealthStatus.Healthy, graph.Evaluate("Platform").Status);
    }

    [Fact]
    public void AddPrognosis_AddComposite_ByName_ReferencesDelegate()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestExternalClient { IsUp = true });

        services.AddPrognosis(health =>
        {
            health.AddDelegate<TestExternalClient>("Email",
                c => HealthStatus.Healthy);

            health.AddComposite("Notifications", n =>
            {
                n.DependsOn("Email", Importance.Optional);
            });
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode("Notifications", out var node));
        Assert.Single(node.Dependencies);
        Assert.Equal("Email", node.Dependencies[0].Node.Name);
    }

    [Fact]
    public void AddPrognosis_AddComposite_WithResilientDependencies()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite("Resilient", c =>
            {
                c.DependsOn<TestDatabaseService>(Importance.Resilient);
                c.DependsOn<TestCacheService>(Importance.Resilient);
            });

            health.MarkAsRoot("Resilient");
        });

        var sp = services.BuildServiceProvider();
        var graph = sp.GetRequiredService<HealthGraph>();

        Assert.True(graph.TryGetNode("Resilient", out var node));
        Assert.Equal(HealthStatus.Healthy, graph.Evaluate("Resilient").Status);
    }

    // ── HealthGraph singleton ───────────────────────────────────────

    [Fact]
    public void AddPrognosis_HealthGraph_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestExternalClient { IsUp = true });
        services.AddPrognosis(health =>
        {
            health.AddDelegate<TestExternalClient>(c => HealthStatus.Healthy);
        });

        var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<HealthGraph>();
        var b = sp.GetRequiredService<HealthGraph>();

        Assert.Same(a, b);
    }

    // ── Error cases ─────────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_AddComposite_MissingDependencyName_Throws()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.AddComposite("Broken", c =>
            {
                c.DependsOn("NonExistent", Importance.Required);
            });
        });

        var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<HealthGraph>());
    }

    [Fact]
    public void AddPrognosis_MarkAsRoot_MissingName_Throws()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.MarkAsRoot("DoesNotExist");
        });

        var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<HealthGraph>());
    }

    [Fact]
    public void AddPrognosis_MultipleRootCandidates_WithoutMarkAsRoot_Throws()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
        });

        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<HealthGraph>());
        Assert.Contains("MarkAsRoot", ex.Message);
    }

    [Fact]
    public void AddPrognosis_AddComposite_MissingDependencyType_Throws()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            // TestDatabaseService is NOT scanned, so it won't be found.
            health.AddComposite("Broken", c =>
            {
                c.DependsOn<TestDatabaseService>(Importance.Required);
            });
        });

        var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<HealthGraph>());
    }

    // ── UseMonitor ──────────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_UseMonitor_RegistersHealthMonitor()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.MarkAsRoot("TestAuth");
            health.UseMonitor(TimeSpan.FromHours(1));
        });

        var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<HealthMonitor>();

        Assert.NotNull(monitor);
        monitor.Dispose();
    }

    // ── Multi-root ──────────────────────────────────────────────────

    [Fact]
    public void AddPrognosis_SingleRoot_GenericMarkAsRoot_RegistersPlainHealthGraph()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);
            health.AddComposite(nameof(TestRootMarkerA), app =>
            {
                app.DependsOn<TestAuthService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Required);
            });
            health.MarkAsRoot<TestRootMarkerA>();
        });

        var sp = services.BuildServiceProvider();

        // Plain HealthGraph is available (single root — no forced generic).
        var graph = sp.GetRequiredService<HealthGraph>();
        Assert.Equal(nameof(TestRootMarkerA), graph.Root.Name);

        // HealthGraph<T> is also available.
        var typed = sp.GetRequiredService<HealthGraph<TestRootMarkerA>>();
        Assert.Same(graph, typed.Graph);
    }

    [Fact]
    public void AddPrognosis_MultipleRoots_KeyedResolution()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite("Ops", app =>
            {
                app.DependsOn<TestDatabaseService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Required);
            });

            health.AddComposite("Customer", app =>
            {
                app.DependsOn<TestAuthService>(Importance.Required);
            });

            health.MarkAsRoot("Ops");
            health.MarkAsRoot("Customer");
        });

        var sp = services.BuildServiceProvider();

        var opsGraph = sp.GetRequiredKeyedService<HealthGraph>("Ops");
        Assert.Equal("Ops", opsGraph.Root.Name);

        var customerGraph = sp.GetRequiredKeyedService<HealthGraph>("Customer");
        Assert.Equal("Customer", customerGraph.Root.Name);

        Assert.NotSame(opsGraph, customerGraph);
    }

    [Fact]
    public void AddPrognosis_MultipleRoots_SharedNodes()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite("Ops", app =>
            {
                app.DependsOn<TestDatabaseService>(Importance.Required);
            });

            health.AddComposite("Customer", app =>
            {
                app.DependsOn<TestDatabaseService>(Importance.Required);
                app.DependsOn<TestCacheService>(Importance.Important);
            });

            health.MarkAsRoot("Ops");
            health.MarkAsRoot("Customer");
        });

        var sp = services.BuildServiceProvider();

        var opsGraph = sp.GetRequiredKeyedService<HealthGraph>("Ops");
        var customerGraph = sp.GetRequiredKeyedService<HealthGraph>("Customer");

        // Both graphs share the same underlying TestDatabase node instance.
        Assert.True(opsGraph.TryGetNode("TestDatabase", out var opsDb));
        Assert.True(customerGraph.TryGetNode("TestDatabase", out var custDb));
        Assert.Same(opsDb, custDb);
    }

    [Fact]
    public void AddPrognosis_MultipleRoots_GenericResolution()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite(nameof(TestRootMarkerA), app =>
            {
                app.DependsOn<TestDatabaseService>(Importance.Required);
            });

            health.AddComposite(nameof(TestRootMarkerB), app =>
            {
                app.DependsOn<TestCacheService>(Importance.Required);
            });

            health.MarkAsRoot<TestRootMarkerA>();
            health.MarkAsRoot<TestRootMarkerB>();
        });

        var sp = services.BuildServiceProvider();

        var graphA = sp.GetRequiredService<HealthGraph<TestRootMarkerA>>();
        Assert.Equal(nameof(TestRootMarkerA), graphA.Root.Name);

        var graphB = sp.GetRequiredService<HealthGraph<TestRootMarkerB>>();
        Assert.Equal(nameof(TestRootMarkerB), graphB.Root.Name);

        // Also available via keyed resolution.
        var keyedA = sp.GetRequiredKeyedService<HealthGraph>(nameof(TestRootMarkerA));
        Assert.Same(graphA.Graph, keyedA);
    }

    [Fact]
    public void AddPrognosis_MultipleRoots_PlainHealthGraph_NotRegistered()
    {
        var services = new ServiceCollection();
        services.AddPrognosis(health =>
        {
            health.ScanForServices(typeof(ServiceCollectionExtensionsTests).Assembly);

            health.AddComposite("A", app => app.DependsOn<TestDatabaseService>(Importance.Required));
            health.AddComposite("B", app => app.DependsOn<TestCacheService>(Importance.Required));

            health.MarkAsRoot("A");
            health.MarkAsRoot("B");
        });

        var sp = services.BuildServiceProvider();

        // Plain HealthGraph should NOT be registered for multi-root.
        Assert.Null(sp.GetService<HealthGraph>());
    }
}

// ── Test fixtures ────────────────────────────────────────────────────

public class TestDatabaseService : IHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("TestDatabase");
}

public class TestCacheService : IHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("TestCache");
}

[DependsOn<TestDatabaseService>(Importance.Required)]
public class TestAuthService : IHealthAware
{
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("TestAuth");
}

public class TestExternalClient
{
    public bool IsUp { get; set; } = true;
}

// Marker types for multi-root tests.
public class TestRootMarkerA;
public class TestRootMarkerB;
