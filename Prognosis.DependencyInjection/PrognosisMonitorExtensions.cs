using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Prognosis.DependencyInjection;

/// <summary>
/// Optional extension for integrating <see cref="HealthMonitor"/> as a
/// hosted service. Rx users can skip this and build their own pipeline
/// from <see cref="HealthGraph.Root"/> instead.
/// </summary>
public static class PrognosisMonitorExtensions
{
    /// <summary>
    /// Registers a <see cref="HealthMonitor"/> backed by the materialized
    /// <see cref="HealthGraph"/> and wraps it in an <see cref="IHostedService"/>
    /// so it starts and stops with the host. The monitor is deadline-aware: it wakes on
    /// the graph's <see cref="HealthGraph.NextTemporalDeadline"/> (policies AND leases)
    /// and re-arms as it moves, so temporal features do not need a hand-rolled pump.
    /// <para>
    /// <paramref name="cadence"/> is optional. Omit it for a graph whose temporal nodes
    /// are all deadline-driven; supply it when a temporal node's input is a drifting
    /// pull-probe whose change has no computable deadline (the monitor then also polls at
    /// least that often). The non-DI equivalent is <c>graph.RunMonitor(cadence)</c>.
    /// </para>
    /// <para>
    /// Rx users can skip this entirely and build their own pipeline:
    /// <code>
    /// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
    /// graph.Root.PollHealthReport(TimeSpan.FromSeconds(30)).Subscribe(...);
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="builder">The Prognosis builder.</param>
    /// <param name="cadence">
    /// Optional fixed poll interval; omit for a purely deadline-driven monitor. Must be
    /// positive when supplied.
    /// </param>
    public static PrognosisBuilder UseMonitor(this PrognosisBuilder builder, TimeSpan? cadence = null)
    {
        builder.Services.AddSingleton(sp =>
        {
            var graph = sp.GetRequiredService<HealthGraph>();
            return new HealthMonitor(graph, cadence);
        });
        builder.Services.AddSingleton<IHostedService, HealthMonitorHostedService>();
        return builder;
    }
}

/// <summary>
/// Adapts <see cref="HealthMonitor"/> to <see cref="IHostedService"/>.
/// </summary>
internal sealed class HealthMonitorHostedService(HealthMonitor monitor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        monitor.Start();
        monitor.Poll();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await monitor.DisposeAsync();
    }
}
