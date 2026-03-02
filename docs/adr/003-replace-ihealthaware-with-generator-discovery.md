# ADR-003: Replace IHealthAware / Assembly Scanning with Source-Generated Discovery

**Status:** Accepted
**Date:** 06-14-2025
**Drivers:** Zero-reflection DI registration, compile-time safety, removal of marker interface

## Context

The DI integration package used three mechanisms to discover and wire services into the health graph:

1. **`IHealthAware` marker interface** — services implemented this interface to expose a `HealthNode` property. Defined in the core `Prognosis` package.
2. **`ScanForServices(Assembly)`** — reflection-based assembly scanning at startup discovered all `IHealthAware` implementations, registered them as singletons, and read `[DependsOn<T>]` class-level attributes to wire edges.
3. **`DependsOnAttribute<T>`** — a generic, class-level attribute that declared typed dependencies between `IHealthAware` implementations.

### Problems

- **Reflection at startup.** `ScanForServices` iterated all types in scanned assemblies via `assembly.GetTypes()`, checked `IsAssignableFrom`, and read custom attributes. This is slow in large assemblies and invisible to AOT/trimming.
- **Marker interface in core.** `IHealthAware` was defined in the core package but only consumed by the DI package. This forced the core to know about a DI-specific concern.
- **Generic attribute constraint.** `DependsOnAttribute<T>` required `where T : class, IHealthAware`, coupling the attribute to the interface and preventing its use on services that didn't implement it.
- **Two name systems.** Names created by the builder at runtime (`AddComposite`, `AddDelegate`) were invisible to the `HealthNodeNameCollector` generator, forcing users to maintain a hand-written `ServiceNames` class alongside the generated `HealthNames`.

## Decision

### 1. Remove `IHealthAware` from the core package

Services no longer need to implement any interface. Any class with a public `HealthNode` property participates in the health graph.

### 2. Replace `ScanForServices` with `AddServiceNode<T>`

A new method on `PrognosisBuilder` explicitly registers a service type and a selector for its `HealthNode`:

```csharp
health.AddServiceNode<DatabaseService>(svc => svc.HealthNode);
health.AddServiceNode<AuthService>(svc => svc.HealthNode, deps =>
{
    deps.DependsOn("Database", Importance.Required);
});
```

Services are registered as singletons via `TryAddSingleton`. No reflection scanning.

### 3. Emit `AddDiscoveredNodes()` via source generator

The new `ServiceNodeDiscoveryGenerator` scans for classes with public `HealthNode` properties, reads `[DependsOn]` attributes on those properties, and emits an `AddDiscoveredNodes()` extension method that calls `AddServiceNode<T>` for each discovered class:

```csharp
// Generated code:
public static PrognosisBuilder AddDiscoveredNodes(this PrognosisBuilder builder)
{
    builder.AddServiceNode<DatabaseService>(svc => svc.HealthNode);
    builder.AddServiceNode<AuthService>(svc => svc.HealthNode, deps =>
    {
        deps.DependsOn("Database", Importance.Required);
    });
    return builder;
}
```

The generator only emits when `PrognosisBuilder` is referenceable in the compilation (i.e., the project references `Prognosis.DependencyInjection`).

### 4. Repurpose `DependsOnAttribute` as property-level, string-based

The attribute moves from class-level generic (`[DependsOn<DatabaseService>]`) to property-level concrete (`[DependsOn("Database", Importance.Required)]`):

```csharp
class AuthService
{
    [DependsOn("Database", Importance.Required)]
    [DependsOn("Cache", Importance.Important)]
    public HealthNode HealthNode { get; } = HealthNode.CreateDelegate("AuthService");
}
```

### 5. Widen `HealthNodeNameCollector` to scan builder methods

The name collector now also extracts names from `PrognosisBuilder.AddComposite("name", ...)` and `AddDelegate<T>("name", ...)` calls, in addition to `HealthNode.CreateDelegate`/`CreateComposite`. This eliminates the need for a separate hand-written constants class. The `DependsOnEdgeAnalyzer` was widened correspondingly.

### 6. Relax `DependencyConfigurator.DependsOn<T>` constraint

The generic `DependsOn<T>` on the configurator was relaxed from `where T : class, IHealthAware` to `where T : class`. It resolves by `typeof(T).Name` — no interface required.

### 7. Relax `HealthGraph.TryGetNode<T>` constraint

Similarly relaxed from `where T : class, IHealthAware` to `where T : class`.

## Consequences

### Positive

- **Zero reflection at startup.** Service registration is fully compile-time generated.
- **AOT/trimming safe.** No `GetTypes()`, no `GetCustomAttributes` at runtime.
- **No marker interface.** Any class with a public `HealthNode` property works — third-party base classes, records, etc.
- **Single name system.** `HealthNames` captures all node names from both factory calls and builder calls.
- **Compile-time validation.** The generator and analyzer validate names and edges before the app runs.

### Negative

- **Breaking change.** `IHealthAware`, `ScanForServices`, and `DependsOnAttribute<T>` are removed. Consumers must migrate to `AddServiceNode<T>` / `AddDiscoveredNodes()` and property-level `[DependsOn]`.
- **Generator dependency.** `AddDiscoveredNodes()` requires the `Prognosis.Generators` analyzer package. Without it, services must be registered manually via `AddServiceNode<T>`.

### Neutral

- `EdgeDefinition` simplified from `(Type?, string?, Importance)` to `(string?, Importance)` — all edges are now name-based.
- `BuildNodePool` no longer maintains a `byType` dictionary — only `byName`.
