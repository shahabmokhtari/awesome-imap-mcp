using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Dashboard;

/// <summary>
/// Decides which network interfaces an HTTP listener binds to.
///
/// The MCP endpoint and the tool API execute mail operations — read, send,
/// delete — and carry no authentication of their own, so anything that can
/// reach the port can drive the mailbox. They therefore bind to loopback
/// unless the operator explicitly asks for something wider.
/// </summary>
public static class ListenAddress
{
    /// <summary>
    /// Resolves a configured <c>bind_address</c>. Returns <c>null</c> for
    /// loopback (the default), <see cref="IPAddress.Any"/> for every interface,
    /// or the specific address requested.
    /// </summary>
    public static IPAddress? Resolve(string? bindAddress)
    {
        var value = bindAddress?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Equals("loopback", StringComparison.OrdinalIgnoreCase))
            return null;

        if (value is "*" or "0.0.0.0" or "+")
            return IPAddress.Any;

        if (!IPAddress.TryParse(value.Trim('[', ']'), out var address))
            throw new ArgumentException(
                $"Invalid bind_address '{bindAddress}'. Use \"localhost\", \"0.0.0.0\", or an IP address.",
                nameof(bindAddress));

        return IPAddress.IsLoopback(address) ? null : address;
    }

    /// <summary>True when the address keeps the listener off the network.</summary>
    public static bool IsLoopbackOnly(string? bindAddress) => Resolve(bindAddress) is null;

    /// <summary>
    /// Binds <paramref name="options"/> to the configured address. Loopback uses
    /// <see cref="KestrelServerOptions.ListenLocalhost(int)"/>, which listens on
    /// both 127.0.0.1 and ::1 — so a client that resolves <c>localhost</c> to
    /// ::1 first is not refused — and tolerates hosts with IPv6 disabled.
    /// </summary>
    public static void Configure(KestrelServerOptions options, string? bindAddress, int port,
        ILogger logger, string listenerName, string exposureWarning)
    {
        var address = Resolve(bindAddress);
        if (address is null)
        {
            options.ListenLocalhost(port);
            return;
        }

        if (address.Equals(IPAddress.Any))
            options.ListenAnyIP(port);
        else
            options.Listen(address, port);

        logger.LogWarning(
            "{Listener} is listening on {Address}:{Port}, reachable from other machines. {Exposure} " +
            "Set server.bind_address to \"localhost\" unless this is intended.",
            listenerName, address, port, exposureWarning);
    }

    /// <summary>Host names a loopback-only listener will answer to.</summary>
    public static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// Registers Host-header filtering for a loopback-only listener.
    ///
    /// Binding to loopback does not stop a browser from reaching the port: a web
    /// page can point its own domain at 127.0.0.1 (DNS rebinding) and then call
    /// the API as a same-origin request. Rejecting any Host header other than a
    /// loopback name defeats that, because the rebound request still carries the
    /// attacker's domain. Wider bindings are left unfiltered — the operator chose
    /// to accept arbitrary hosts.
    /// </summary>
    public static void AddHostFiltering(IServiceCollection services, string? bindAddress)
    {
        if (!IsLoopbackOnly(bindAddress))
            return;

        services.AddHostFiltering(o =>
        {
            o.AllowedHosts = [.. LoopbackHosts];
            o.IncludeFailureMessage = false;
        });
    }

    /// <summary>Adds the filter to the pipeline; call before mapping any endpoint.</summary>
    public static void UseHostFiltering(IApplicationBuilder app, string? bindAddress)
    {
        if (IsLoopbackOnly(bindAddress))
            app.UseHostFiltering();
    }
}
