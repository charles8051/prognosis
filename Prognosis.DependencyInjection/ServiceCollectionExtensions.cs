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
    /// Configures the Prognosis health graph and registers one or more
    /// <see cref="HealthGraph"/> singletons that materialize the full graph
    /// (scanned services, composites, delegates, attribute-declared edges)
    /// on first resolution.
    /// <para>
    /// Call <see cref="PrognosisBuilder.MarkAsRoot(string)"/> or
    /// <see cref="PrognosisBuilder.MarkAsRoot{T}"/> one or more times to
    /// designate roots. Each root produces a separate <see cref="HealthGraph"/>
    /// that shares the same underlying nodes.
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Zero roots</b> — the single root is auto-detected (the unique
    ///     node that is not a dependency of any other node).
    ///   </item>
    ///   <item>
    ///     <b>One root</b> — a plain <see cref="HealthGraph"/> singleton
    ///     is registered (plus <see cref="HealthGraph{TRoot}"/> when
    ///     <see cref="PrognosisBuilder.MarkAsRoot{T}"/> was used).
    ///   </item>
    ///   <item>
    ///     <b>Multiple roots</b> — each graph is registered as a keyed
    ///     <see cref="HealthGraph"/> (keyed by the root name) and optionally
    ///     as <see cref="HealthGraph{TRoot}"/>.
    ///   </item>
    /// </list>
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

        // 2. Build the shared node pool once (lazily, on first graph resolution).
        //    Every HealthGraph rooted at a different node shares these nodes.
        var poolLock = new object();
        Dictionary<string, HealthNode>? pool = null;

        Dictionary<string, HealthNode> EnsurePool(IServiceProvider sp)
        {
            if (pool is not null) return pool;
            lock (poolLock)
            {
                return pool ??= BuildNodePool(sp, builder);
            }
        }

        // 3. Determine roots to register.
        var roots = builder.Roots;

        if (roots.Count == 0)
        {
            // Auto-detect: register as plain HealthGraph.
            services.AddSingleton(sp =>
            {
                var p = EnsurePool(sp);
                var rootName = AutoDetectSingleRoot(p);
                return new HealthGraph(p[rootName]);
            });
        }
        else if (roots.Count == 1)
        {
            // Single declared root: register as plain HealthGraph.
            var name = roots[0].Name;
            services.AddSingleton(sp =>
            {
                var p = EnsurePool(sp);
                return ResolveRootGraph(p, name);
            });

            // Also register HealthGraph<T> if the generic overload was used.
            roots[0].RegisterTyped?.Invoke(services,
                sp => sp.GetRequiredService<HealthGraph>());
        }
        else
        {
            // Multiple roots: register keyed HealthGraph per root.
            foreach (var rootDef in roots)
            {
                var name = rootDef.Name;
                services.AddKeyedSingleton<HealthGraph>(name, (sp, _) =>
                {
                    var p = EnsurePool(sp);
                    return ResolveRootGraph(p, name);
                });

                // Also register HealthGraph<T> if the generic overload was used.
                rootDef.RegisterTyped?.Invoke(services,
                    sp => sp.GetRequiredKeyedService<HealthGraph>(name));
            }
        }

        return services;
    }

    // ── Node pool construction ───────────────────────────────────────

    private static Dictionary<string, HealthNode> BuildNodePool(
        IServiceProvider sp, PrognosisBuilder builder)
    {
        var byType = new Dictionary<Type, HealthNode>();
        var byName = new Dictionary<string, HealthNode>();

        foreach (var svc in sp.GetServices<IHealthAware>())
        {
            var health = svc.HealthNode;
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

                kvp.Value.DependsOn(dep, attr.Importance);
            }
        }

        // Build delegate wrappers.
        foreach (var def in builder.Delegates)
        {
            var d = HealthNode.CreateDelegate(def.Name, () => def.HealthCheck(sp));
            WireEdges(d, def.Edges, byType, byName);
            byName[def.Name] = d;
        }

        // Build composites (order matters — later composites can reference earlier ones).
        foreach (var def in builder.Composites)
        {
            var composite = HealthNode.CreateComposite(def.Name);
            WireEdges(composite, def.Edges, byType, byName);
            byName[def.Name] = composite;
        }

        return byName;
    }

    // ── Root resolution helpers ──────────────────────────────────────

    private static HealthGraph ResolveRootGraph(
        Dictionary<string, HealthNode> pool, string rootName)
    {
        if (!pool.TryGetValue(rootName, out var rootNode))
            throw new InvalidOperationException(
                $"MarkAsRoot specified '{rootName}', but no node with that name exists in the health graph.");

        return new HealthGraph(rootNode);
    }

    private static string AutoDetectSingleRoot(Dictionary<string, HealthNode> pool)
    {
        var allNodes = pool.Values.ToArray();
        var children = new HashSet<HealthNode>(ReferenceEqualityComparer.Instance);
        foreach (var node in allNodes)
        {
            foreach (var dep in node.Dependencies)
                children.Add(dep.Node);
        }

        var roots = allNodes.Where(n => !children.Contains(n)).ToArray();

        if (roots.Length == 0)
            throw new InvalidOperationException(
                "No root node could be determined — every node is a dependency of another. " +
                "Use MarkAsRoot to designate the root explicitly.");

        if (roots.Length > 1)
            throw new InvalidOperationException(
                $"Multiple root candidates found ({string.Join(", ", roots.Select(r => $"'{r.Name}'"))}). " +
                "Use MarkAsRoot to designate a single root, or add a composite node that depends on all top-level nodes.");

        return roots[0].Name;
    }

    // ── Edge wiring ──────────────────────────────────────────────────

    private static void WireEdges(
        HealthNode target,
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

    private static HealthNode ResolveEdge(
        EdgeDefinition edge,
        Dictionary<Type, HealthNode> byType,
        Dictionary<string, HealthNode> byName)
    {
        if (edge.ServiceType is not null)
        {
            return byType.TryGetValue(edge.ServiceType, out var node)
                ? node
                : throw new InvalidOperationException(
                    $"Dependency type '{edge.ServiceType.Name}' was not found in the health graph.");
        }

        return byName.TryGetValue(edge.ServiceName!, out var named)
            ? named
            : throw new InvalidOperationException(
                $"Dependency '{edge.ServiceName}' was not found in the health graph.");
    }
    }

