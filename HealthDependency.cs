namespace Prognosis;

/// <summary>
/// A weighted edge from a parent service to one of its dependencies.
/// </summary>
public sealed record HealthDependency(HealthNode Service, Importance Importance);
