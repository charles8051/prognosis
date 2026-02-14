namespace ServiceHealthModel;

/// <summary>
/// Extended contract for services that support push-based health notifications.
/// <see cref="IObservable{T}"/> is a BCL type — consumers add System.Reactive
/// only when they want operators like <c>DistinctUntilChanged</c> or <c>Throttle</c>.
/// </summary>
public interface IObservableServiceHealth : IServiceHealth
{
    /// <summary>
    /// Emits the new <see cref="HealthStatus"/> each time the service's
    /// effective health changes.
    /// </summary>
    IObservable<HealthStatus> StatusChanged { get; }

    /// <summary>
    /// Re-evaluates the current health and pushes a notification through
    /// <see cref="StatusChanged"/> if the status has changed.
    /// Call this when the service knows something has changed, or let a
    /// <see cref="HealthMonitor"/> call it on a polling interval.
    /// </summary>
    void NotifyChanged();
}
