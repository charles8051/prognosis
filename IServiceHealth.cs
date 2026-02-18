namespace Prognosis;

/// <summary>
/// Marker interface for classes that participate in the health graph by
/// embedding a <see cref="ServiceHealth"/> property. Implement this on your
/// own service classes so the DI scanner can discover them automatically;
/// there is no other member to implement.
/// </summary>
public interface IServiceHealth
{
    /// <summary>
    /// The <see cref="ServiceHealth"/> node that represents this service in
    /// the health graph. Typically a <see cref="DelegatingServiceHealth"/>
    /// (when the service has its own intrinsic health check) or a
    /// <see cref="CompositeServiceHealth"/> (when health is derived entirely
    /// from sub-dependencies).
    /// </summary>
    ServiceHealth Health { get; }
}
