# Copilot Instructions for Prognosis

## Project Context

- Always read and reference `context.md` in the repository root before making changes. It contains a condensed summary of the architecture, core types, threading model, propagation flow, and conventions. Use it to ground decisions in the actual design rather than guessing.
- Always keep documentation up to date.
- Always keep tests, benchmarks, and examples up to date.

## Code Style

- Target .NET Standard 2.0 and 2.1. Use C# latest language features but ensure compatibility via polyfills in `Polyfills/`.
- Prefer sealed classes and records. `HealthNode` is sealed with private constructors — create instances via `CreateDelegate` or `CreateComposite` factory methods.
- Use copy-on-write for concurrent collections (volatile `IReadOnlyList<T>` references, replace under lock, readers never lock).
- Do not add comments unless they match existing style or explain non-obvious concurrency/threading behavior.
- Follow existing naming: `_camelCase` for fields, `PascalCase` for methods/properties, `s_camelCase` for statics.

## Architecture Rules

- `HealthGraph` is the sole public surface for querying, reporting, and observables. `HealthNode` owns topology building and intrinsic checks.
- Observer notifications must always fire **outside** `_propagationLock` to prevent re-entrant deadlocks.
- Lock ordering: `_propagationLock` → `_topologyLock` → observer locks (independent).
- Extension packages (`Prognosis.DependencyInjection`, `Prognosis.Reactive`) reference only the core. Never add reverse references.

## Testing

- Tests use xUnit. Test projects target net10.0.
- Create nodes via `HealthNode.CreateDelegate(...)` / `HealthNode.CreateComposite(...)` — never call constructors directly.
- Reactive tests may use `Microsoft.Reactive.Testing`.
