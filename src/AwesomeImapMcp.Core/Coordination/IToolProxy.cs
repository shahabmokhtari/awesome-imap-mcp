namespace AwesomeImapMcp.Core.Coordination;

/// <summary>
/// Abstraction for proxying MCP tool calls to a remote primary instance.
/// Used by secondary instances in multi-instance deployments.
/// </summary>
public interface IToolProxy
{
    /// <summary>Proxies a synchronous tool call to the primary instance.</summary>
    string Execute(string toolName, Dictionary<string, object?> parameters);

    /// <summary>Proxies an async tool call to the primary instance.</summary>
    Task<string> ExecuteAsync(string toolName, Dictionary<string, object?> parameters);
}
