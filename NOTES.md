# API Audit Notes

Issues identified during a consumer-perspective audit of the Prognosis public API.
Ordered by severity. Each item is self-contained — tackle in any order.

> **Status (v4.0.0-alpha):** All items below have been addressed or are no longer applicable.
> The file references pre-refactor type names (`HealthCheck`, `HealthTracker`, `HealthAggregator`, `Diff`)
> which have since been renamed or removed. Retained for historical context only.

---

## 1. `HealthMonitor` starts polling before subscribers can attach

**Severity:** Medium
**File:** `HealthMonitor.cs` — constructor (line ~29)

The polling loop starts as a fire-and-forget task in the constructor:

```csharp
_pollingTask = PollLoopAsync(_cts.Token);
```

If the timer fires before the caller calls `.ReportChanged.Subscribe(...)`, the first status change is lost. The DI hosted service works around this with an explicit `Poll()` in `StartAsync`, but direct consumers get a race condition.

**Fix options:**
- Remove `PollLoopAsync` from the constructor; add an explicit `Start()` method.
- Defer loop start to the first `Subscribe` call.
- Accept the race and document that `Poll()` should be called after subscribing to establish baseline.

---

## 2. `HealthCheck` doesn't validate `name`

**Severity:** Medium
**File:** `HealthCheck.cs` — constructor (line ~27)

`HealthGroup` validates that `name` isn't null/whitespace, but `HealthCheck` accepts anything silently:

```csharp
var svc = new HealthCheck("", () => HealthStatus.Healthy); // no error
```

Since `Name` is used as the key in `HealthGraph`, `Diff`, `HealthSnapshot`, and `EvaluateAll`, a blank name creates silent collisions. Validation should be consistent across all shipped `IHealthAware` implementations.

**Fix:** Add the same `ArgumentException` guard as `HealthGroup`.

---

## 3. `Diff` doesn't report removed node

**Severity:** Low–Medium
**File:** `HealthAggregator.cs` — `Diff` method (lines ~141–172)

The method detects node that *appear* in the new report (reports them as `Unknown → Current`) but silently ignores node that *disappear*. If a service existed in `previous` but is missing from `current`, no `StatusChange` is emitted.

**Fix:** After the forward pass, iterate `previous.node` and emit a `Current → Unknown` (or a dedicated "Removed" status) for any service not present in `current`.

---

## 4. `HealthTracker.DependsOn` has no thread safety

**Severity:** Low–Medium
**File:** `HealthTracker.cs` — `DependsOn` method (line ~53)

`_dependencies` is a plain `List<HealthDependency>` mutated by `DependsOn()` and iterated by `Evaluate()` (via the aggregation strategy). The `_lock` object protects `_observers` and `_lastEmitted`, but `_dependencies` is unprotected. Concurrent `DependsOn()` + `Evaluate()` from different threads can throw `InvalidOperationException` (collection modified during enumeration).

In practice dependencies are usually wired at startup, but the fluent API doesn't communicate that constraint.

**Fix options:**
- Document that `DependsOn` must be called before the service participates in evaluation.
- Use a `ConcurrentBag` or protect reads/writes with the existing lock.
- Make `DependsOn` only callable before first `Evaluate` (throw after).

---

## 5. `HealthReportComparer.GetHashCode` is weak

**Severity:** Low
**File:** `HealthReportComparer.cs` (line ~34)

```csharp
public int GetHashCode(HealthReport obj) =>
    obj.OverallStatus.GetHashCode();
```

Returns one of 4 possible hash codes. In a `HashSet<HealthReport>` or `Dictionary` keyed by reports, this degrades to O(n) lookups. Correctness is fine since `Equals` does a full comparison.

**Fix:** Fold in `node.Count` and a few service statuses:

```csharp
public int GetHashCode(HealthReport obj)
{
    var hash = new HashCode();
    hash.Add(obj.OverallStatus);
    hash.Add(obj.node.Count);
    foreach (var svc in obj.node)
    {
        hash.Add(svc.Name);
        hash.Add(svc.Status);
    }
    return hash.ToHashCode();
}
```

---

## 6. `HealthMonitor` implements `IAsyncDisposable` but not `IDisposable`

**Severity:** Low
**File:** `HealthMonitor.cs` (line ~9)

Consumers who try `using var monitor = ...` (synchronous) get a compile error — they must use `await using`. This is technically correct for an async resource, but in non-hosted scenarios (console apps, tests) it adds friction. Some libraries implement both.

**Fix options:**
- Add `IDisposable` that blocks on `DisposeAsync()`.
- Document that `await using` is required.
- Leave as-is — this is an intentional design choice.

---

## 7. Rx extensions accept `IHealthAware[]`, not `IEnumerable<IHealthAware>`

**Severity:** Low
**File:** `Prognosis.Reactive/HealthRxExtensions.cs` (lines ~18, ~40)

```csharp
public static IObservable<HealthReport> PollHealthReport(
    this IHealthAware[] roots, TimeSpan interval)
```

Consumers holding a `List<IHealthAware>` or `IReadOnlyList<IHealthAware>` must call `.ToArray()`. `HealthMonitor` accepts `IEnumerable<IHealthAware>`, so the inconsistency is surprising.

**Fix:** Change parameter to `IReadOnlyList<IHealthAware>` (or keep the array overload and add an `IEnumerable` overload that calls `.ToArray()` internally).

---

## 8. `StatusChanged` allocates a new observable on every property access

**Severity:** Low
**Files:** `HealthTracker.cs` (line ~51), `HealthMonitor.cs` (line ~23)

```csharp
public IObservable<HealthStatus> StatusChanged => new StatusObservable(this);
```

Every access allocates. The observable is stateless so this is functionally harmless, but `ReferenceEquals(svc.StatusChanged, svc.StatusChanged)` returns `false`, which is surprising. Caching in a `readonly` field is trivial.

**Fix:** Initialize once in the constructor or as a field initializer:

```csharp
private readonly IObservable<HealthStatus> _statusChanged;
// in ctor: _statusChanged = new StatusObservable(this);
public IObservable<HealthStatus> StatusChanged => _statusChanged;
```
