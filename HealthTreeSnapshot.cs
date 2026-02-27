namespace Prognosis;

/// <summary>
/// A point-in-time, tree-shaped capture of a node and its full dependency
/// subgraph. Unlike <see cref="HealthReport"/> (which is a flat list suited
/// for diffing and reactive pipelines), this type preserves the dependency
/// hierarchy — making it ideal for JSON serialization where nesting should
/// mirror topology.
/// </summary>
/// <param name="Name">Display name of the service.</param>
/// <param name="Status">The evaluated health status.</param>
/// <param name="Reason">
/// An optional explanation (e.g. "Connection pool exhausted").
/// Typically <see langword="null"/> when healthy.
/// </param>
/// <param name="Dependencies">
/// The node's direct dependencies, each paired with its
/// <see cref="Importance"/> weight.
/// </param>
public sealed record HealthTreeSnapshot(
    string Name,
    HealthStatus Status,
    string? Reason,
    IReadOnlyList<HealthTreeDependency> Dependencies);

/// <summary>
/// A weighted edge in a <see cref="HealthTreeSnapshot"/> tree. Pairs the
/// evaluated subtree with the <see cref="Importance"/> the parent assigns
/// to this dependency.
/// </summary>
/// <param name="Importance">How failures in this dependency propagate to the parent.</param>
/// <param name="Node">The evaluated dependency subtree.</param>
public sealed record HealthTreeDependency(
    Importance Importance,
    HealthTreeSnapshot Node);
