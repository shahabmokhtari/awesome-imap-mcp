using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Dashboard;
using AwesomeImapMcp.McpServer.Tools;

namespace AwesomeImapMcp.McpServer.Tests.Tools;

/// <summary>
/// Both the MCP HTTP transport and the dashboard's Tools page build tool classes
/// from their own DI containers, which only contain what is forwarded from the
/// root host. A tool whose constructor needs an unforwarded service fails at call
/// time with "Unable to resolve service" (issue #16 and its AccountsStore
/// follow-up). These tests check every tool against both forwarding lists.
/// </summary>
public class ToolDependencyForwardingTests
{
    public static TheoryData<string> Hosts => new() { "dashboard", "mcp-http" };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EveryToolConstructorDependency_IsForwarded(string host)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new StandInRootProvider();
        if (host == "dashboard")
            DashboardHost.ForwardRootServices(services, root, new AppConfig());
        else
            HttpMcpTransportHost.ForwardRootServices(services, root, new AppConfig());

        var registered = services.Select(d => d.ServiceType).ToHashSet();
        var missing = new List<string>();

        foreach (var toolType in ToolTypes())
        {
            var ctor = toolType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            foreach (var p in ctor.GetParameters())
            {
                if (p.HasDefaultValue) continue;
                if (p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>)) continue;
                if (!registered.Contains(p.ParameterType))
                    missing.Add($"{toolType.Name}({p.ParameterType.Name} {p.Name})");
            }
        }

        Assert.True(missing.Count == 0, $"Not forwarded into the {host} container: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ToolDiscovery_FindsTheToolClasses()
    {
        // Guards the test above against silently checking nothing.
        var types = ToolTypes().ToList();
        Assert.Contains(typeof(AccountManagementTools), types);
        Assert.True(types.Count >= 10, $"only {types.Count} tool types found");
    }

    private static IEnumerable<Type> ToolTypes() =>
        typeof(AccountManagementTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

    /// <summary>
    /// Root container stand-in: hands back an uninitialised placeholder for any
    /// concrete type and a no-op proxy for interfaces. Only registration is under
    /// test, so nothing is ever called on these.
    /// </summary>
    private sealed class StandInRootProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (!serviceType.IsInterface)
                return RuntimeHelpers.GetUninitializedObject(serviceType);
            var create = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
                .MakeGenericMethod(serviceType, typeof(NoOpProxy));
            return create.Invoke(null, null);
        }
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
