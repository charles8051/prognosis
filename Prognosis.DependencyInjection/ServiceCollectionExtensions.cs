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
    /// (service nodes, composites, delegates, dependency edges)
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

        // Register service types from AddServiceNode calls as singletons.
        foreach (var def in builder.ServiceNodes)
        {
            services.TryAddSingleton(def.ServiceType);
        }

        // Build the shared node pool once (lazily, on first graph resolution).
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

        // Determine roots to register.
        var roots = builder.Roots;

        if (roots.Count == 0)
        {
            services.AddSingleton(sp =>
            {
                var p = EnsurePool(sp);
                var rootName = AutoDetectSingleRoot(p);
                return new HealthGraph(p[rootName]);
            });
        }
        else if (roots.Count == 1)
        {
            var name = roots[0].Name;
            services.AddSingleton(sp =>
            {
                var p = EnsurePool(sp);
                return ResolveRootGraph(p, name);
            });

            roots[0].RegisterTyped?.Invoke(services,
                sp => sp.GetRequiredService<HealthGraph>());
        }
        else
        {
            foreach (var rootDef in roots)
            {
                var name = rootDef.Name;
                services.AddKeyedSingleton<HealthGraph>(name, (sp, _) =>
                {
                    var p = EnsurePool(sp);
                    return ResolveRootGraph(p, name);
                });

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
        var byName = new Dictionary<string, HealthNode>();

        // Resolve service nodes registered via AddServiceNode<T>.
        foreach (var def in builder.ServiceNodes)
        {
            var node = def.NodeSelector(sp);
            byName[node.Name] = node;
            WireEdges(node, def.Edges, byName);
        }

        // Build delegate wrappers.
        foreach (var def in builder.Delegates)
        {
            var d = HealthNode.CreateDelegate(def.Name, () => def.HealthCheck(sp));
            WireEdges(d, def.Edges, byName);
            byName[def.Name] = d;
        }

        // Build composites (order matters — later composites can reference earlier ones).
        foreach (var def in builder.Composites)
        {
            var composite = HealthNode.CreateComposite(def.Name);
            WireEdges(composite, def.Edges, byName);
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
        var children = new HashSet<HealthNode>(Polyfills.ReferenceEqualityComparer.Instance);
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
        Dictionary<string, HealthNode> byName)
    {
        foreach (var edge in edges)
        {
            if (!byName.TryGetValue(edge.ServiceName!, out var dep))
                throw new InvalidOperationException(
                    $"Dependency '{edge.ServiceName}' was not found in the health graph.");

            target.DependsOn(dep, edge.Importance);
        }
    }
}
