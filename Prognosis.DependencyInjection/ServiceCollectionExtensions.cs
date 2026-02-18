using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Prognosis.DependencyInjection;

/// <summary>
/// Extension methods for registering the Prognosis health graph in a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures the Prognosis health graph and registers a
    /// <see cref="HealthGraph"/> singleton that materializes the full graph
    /// (scanned services, composites, delegates, attribute-declared edges)
    /// on first resolution.
    /// </summary>
    public static IServiceCollection AddPrognosis(
        this IServiceCollection services,
        Action<PrognosisBuilder> configure)
    {
        var builder = new PrognosisBuilder(services);
        configure(builder);

        // 1. Scan assemblies — register IHealthAware implementations as singletons.
        foreach (var assembly in builder.ScanAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: false, IsInterface: false }
                    && typeof(IHealthAware).IsAssignableFrom(type))
                {
                    services.TryAddSingleton(type);
                    services.AddSingleton(
                        typeof(IHealthAware),
                        sp => (IHealthAware)sp.GetRequiredService(type));
                }
            }
        }

        // 2. Register HealthGraph — materializes the full graph on first resolution.
        services.AddSingleton(sp => MaterializeGraph(sp, builder));

        return services;
    }

    private static HealthGraph MaterializeGraph(IServiceProvider sp, PrognosisBuilder builder)
    {
        // Collect all scanned IHealthAware instances, keyed by their
        // concrete type and by the Health node's name.
        var byType = new Dictionary<Type, HealthNode>();
        var byName = new Dictionary<string, HealthNode>();

        foreach (var svc in sp.GetServices<IHealthAware>())
        {
            var health = svc.Health;
            byType[svc.GetType()] = health;
            byName[health.Name] = health;
        }

        // Wire attribute-declared [DependsOn<T>] edges.
        foreach (var kvp in byType)
        {
            var attrs = kvp.Key.GetCustomAttributes<DependsOnAttribute>();
            foreach (var attr in attrs)
            {
                if (!byType.TryGetValue(attr.DependencyType, out var dep))
                    continue;

                if (kvp.Value is HealthCheck delegating)
                {
                    delegating.DependsOn(dep, attr.Importance);
                }
            }
        }

        // Build delegate wrappers.
        foreach (var def in builder.Delegates)
        {
            var d = new HealthCheck(def.Name, () => def.HealthCheck(sp));
            WireEdges(d, def.Edges, byType, byName);
            byName[def.Name] = d;
        }

        // Build composites (order matters — later composites can reference earlier ones).
        foreach (var def in builder.Composites)
        {
            var deps = ResolveEdges(def.Edges, byType, byName);
            var composite = new HealthGroup(def.Name, deps, def.Aggregator);
            byName[def.Name] = composite;
        }

        // Resolve declared roots.
        var roots = builder.RootNames
            .Select(n => byName.TryGetValue(n, out var s)
                ? s
                : throw new InvalidOperationException(
                    $"Root service '{n}' was not found in the health graph. " +
                    $"Available services: {string.Join(", ", byName.Keys)}"))
            .ToArray();

        return new HealthGraph(roots, byName);
    }

    private static void WireEdges(
        HealthCheck target,
        List<EdgeDefinition> edges,
        Dictionary<Type, HealthNode> byType,
        Dictionary<string, HealthNode> byName)
    {
        foreach (var edge in edges)
        {
            var dep = ResolveEdge(edge, byType, byName);
            target.DependsOn(dep, edge.Importance);
        }
    }

    private static List<HealthDependency> ResolveEdges(
        List<EdgeDefinition> edges,
        Dictionary<Type, HealthNode> byType,
        Dictionary<string, HealthNode> byName)
    {
        return edges
            .Select(e => new HealthDependency(ResolveEdge(e, byType, byName), e.Importance))
            .ToList();
    }

    private static HealthNode ResolveEdge(
        EdgeDefinition edge,
        Dictionary<Type, HealthNode> byType,
        Dictionary<string, HealthNode> byName)
    {
        if (edge.ServiceType is not null)
        {
            return byType.TryGetValue(edge.ServiceType, out var svc)
                ? svc
                : throw new InvalidOperationException(
                    $"Dependency type '{edge.ServiceType.Name}' was not found in the health graph.");
        }

        return byName.TryGetValue(edge.ServiceName!, out var named)
            ? named
            : throw new InvalidOperationException(
                $"Dependency '{edge.ServiceName}' was not found in the health graph.");
    }

    }
