namespace Prognosis;

/// <summary>
/// A weighted edge from a parent service to one of its dependencies.
/// </summary>
public sealed record ServiceDependency(ServiceHealth Service, ServiceImportance Importance);
