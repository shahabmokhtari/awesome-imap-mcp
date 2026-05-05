using Microsoft.Extensions.DependencyInjection;
using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.Dashboard;

/// <summary>
/// Extension methods to register dashboard services into the host DI container.
/// </summary>
public static class DashboardServiceExtensions
{
    /// <summary>
    /// Registers the DashboardHost background service and the shared EventBus.
    /// The DashboardHost checks dashboard_enabled internally, but conventionally
    /// this method is only called when the dashboard is enabled.
    /// </summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services, AppConfig config)
    {
        services.AddSingleton<IEventBus, EventBus>();
        services.AddHostedService<DashboardHost>();

        return services;
    }
}
