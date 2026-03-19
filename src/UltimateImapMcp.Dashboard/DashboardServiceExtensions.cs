using Microsoft.Extensions.DependencyInjection;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.Dashboard;

/// <summary>
/// Extension methods to register dashboard services into the host DI container.
/// </summary>
public static class DashboardServiceExtensions
{
    /// <summary>
    /// Registers all dashboard services including the conditional DashboardHost,
    /// EventBus, and hub relay. Call only when dashboard_enabled is true.
    /// </summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services, AppConfig config)
    {
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<DashboardAuthRepository>();
        services.AddHostedService<DashboardHost>();
        services.AddHostedService<DashboardHubRelay>();

        return services;
    }
}
