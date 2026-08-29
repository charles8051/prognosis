# Prognosis.Generators

Source generators and analyzers for the [Prognosis](https://www.nuget.org/packages/Prognosis) service
health graph. Turns node names into compile-time constants, wires discovered nodes into DI without
reflection, and catches broken `DependsOn` edges before they reach a running graph.

This is a build-time package. It ships no runtime assembly and adds no dependency to your output.

## Installation

```
dotnet add package Prognosis.Generators
```

The package is a development dependency, so it flows into your build and not into anything that
consumes your library.

## What it generates

### `HealthNames` — node names as constants

Every node name declared in your project becomes a constant, so a typo is a build error rather than a
node that silently never matches:

```csharp
// Instead of: report.Nodes.First(n => n.Name == "Database")
report.Nodes.First(n => n.Name == HealthNames.Database);
```

### `AddDiscoveredNodes()` — zero-reflection DI wiring

Discovers annotated services at compile time and emits their registration directly, so the graph is
built without assembly scanning. This is what makes the DI path AOT- and trim-safe; see
[ADR-003](https://github.com/charles8051/prognosis/blob/main/docs/adr/003-replace-ihealthaware-with-generator-discovery.md)
for why reflection was removed.

```csharp
services.AddDiscoveredNodes();
```

## What it diagnoses

| ID | Severity | Meaning |
|---|---|---|
| `PROGNOSIS001` | Warning | A `DependsOn` edge names a node that does not exist. |

An edge that names a missing node is not a runtime error in Prognosis — the dependency simply never
resolves and the parent's health is computed without it. The analyzer exists because that failure is
silent and looks like a healthy graph.

## Requirements

- .NET Standard 2.0 target for the analyzer itself; usable from any project the Roslyn version in your
  SDK supports.
- Pairs with [Prognosis](https://www.nuget.org/packages/Prognosis) and, for `AddDiscoveredNodes()`,
  [Prognosis.DependencyInjection](https://www.nuget.org/packages/Prognosis.DependencyInjection).
