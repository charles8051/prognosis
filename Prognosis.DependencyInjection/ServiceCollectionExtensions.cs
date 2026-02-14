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

        // 1. Scan assemblies — register IServiceHealth implementations as singletons.
        foreach (var assembly in builder.ScanAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: false, IsInterface: false }
                    && typeof(IServiceHealth).IsAssignableFrom(type))
                {
                    services.TryAddSingleton(type);
                    services.AddSingleton(
                        typeof(IServiceHealth),
                        sp => sp.GetRequiredService(type));
                }
            }
        }

        // 2. Register HealthGraph — materializes the full graph on first resolution.
        services.AddSingleton(sp => MaterializeGraph(sp, builder));

        return services;
    }

    private static HealthGraph MaterializeGraph(IServiceProvider sp, PrognosisBuilder builder)
    {
        // Collect all scanned IServiceHealth instances keyed by type and name.
        var byType = new Dictionary<Type, IServiceHealth>();
        var byName = new Dictionary<string, IServiceHealth>();

        foreach (var svc in sp.GetServices<IServiceHealth>())
        {
            byType[svc.GetType()] = svc;
            byName[svc.Name] = svc;
        }

        // Wire attribute-declared [DependsOn<T>] edges.
        foreach (var kvp in byType)
        {
            var attrs = kvp.Key.GetCustomAttributes<DependsOnAttribute>();
            foreach (var attr in attrs)
            {
                if (!byType.TryGetValue(attr.DependencyType, out var dep))
                    continue;

                if (kvp.Value is DelegatingServiceHealth delegating)
                {
                    delegating.DependsOn(dep, attr.Importance);
                }
                else if (FindTracker(kvp.Value) is { } tracker)
                {
                    tracker.DependsOn(dep, attr.Importance);
                }
            }
        }

        // Build delegate wrappers.
        foreach (var def in builder.Delegates)
        {
            var d = new DelegatingServiceHealth(def.Name, () => def.HealthCheck(sp));
            WireEdges(d, def.Edges, byType, byName);
            byName[def.Name] = d;
        }

        // Build composites (order matters — later composites can reference earlier ones).
        foreach (var def in builder.Composites)
        {
            var deps = ResolveEdges(def.Edges, byType, byName);
            var composite = new CompositeServiceHealth(def.Name, deps);
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
        DelegatingServiceHealth target,
        List<EdgeDefinition> edges,
        Dictionary<Type, IServiceHealth> byType,
        Dictionary<string, IServiceHealth> byName)
    {
        foreach (var edge in edges)
        {
            var dep = ResolveEdge(edge, byType, byName);
            target.DependsOn(dep, edge.Importance);
        }
    }

    private static List<ServiceDependency> ResolveEdges(
        List<EdgeDefinition> edges,
        Dictionary<Type, IServiceHealth> byType,
        Dictionary<string, IServiceHealth> byName)
    {
        return edges
            .Select(e => new ServiceDependency(ResolveEdge(e, byType, byName), e.Importance))
            .ToList();
    }

    private static IServiceHealth ResolveEdge(
        EdgeDefinition edge,
        Dictionary<Type, IServiceHealth> byType,
        Dictionary<string, IServiceHealth> byName)
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

    /// <summary>
    /// Locates an embedded <see cref="ServiceHealthTracker"/> field on an
    /// <see cref="IServiceHealth"/> instance via reflection. This supports
    /// the recommended pattern of embedding a tracker and delegating to it.
    /// </summary>
    private static ServiceHealthTracker? FindTracker(IServiceHealth service)
    {
        var field = service.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.FieldType == typeof(ServiceHealthTracker));

        return field?.GetValue(service) as ServiceHealthTracker;
    }
}
