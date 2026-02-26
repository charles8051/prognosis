using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Prognosis.DependencyInjection;

/// <summary>
/// Optional extension for integrating <see cref="HealthMonitor"/> as a
/// hosted service. Rx users can skip this and build their own pipeline
/// from <see cref="HealthGraph.Roots"/> instead.
/// </summary>
public static class PrognosisMonitorExtensions
{
    /// <summary>
    /// Registers a <see cref="HealthMonitor"/> backed by the materialized
    /// <see cref="HealthGraph"/> and wraps it in an <see cref="IHostedService"/>
    /// so it starts and stops with the host.
    /// <para>
    /// Rx users can skip this entirely and build their own pipeline:
    /// <code>
    /// var graph = serviceProvider.GetRequiredService&lt;HealthGraph&gt;();
    /// graph.Roots.PollHealthReport(TimeSpan.FromSeconds(30)).Subscribe(...);
    /// </code>
    /// </para>
    /// </summary>
    public static PrognosisBuilder UseMonitor(this PrognosisBuilder builder, TimeSpan interval)
    {
        builder.Services.AddSingleton(sp =>
        {
            var graph = sp.GetRequiredService<HealthGraph>();
            return new HealthMonitor(graph, interval);
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
