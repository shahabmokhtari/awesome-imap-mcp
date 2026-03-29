using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.McpServer.Tools;

/// <summary>
/// Shared JSON serialization defaults for all MCP tool responses.
/// </summary>
internal static class McpJsonDefaults
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Serializes an error response in the standard { error: "message" } format.</summary>
    internal static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, Options);

    internal static string LogToolCall(ILogger logger, string toolName,
        Dictionary<string, object?> parameters, Func<string> execute, AppConfig config)
    {
        if (!config.Server.LogToolCalls)
            return execute();

        var sw = Stopwatch.StartNew();
        string? result = null;
        Exception? error = null;
        try
        {
            result = execute();
            return result;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            var paramJson = JsonSerializer.Serialize(parameters, Options);
            if (error is not null)
                logger.LogWarning("MCP Tool {Tool} FAILED in {Duration}ms | Params: {Params} | Error: {Error}",
                    toolName, sw.ElapsedMilliseconds, paramJson, error.Message);
            else
                logger.LogInformation("MCP Tool {Tool} OK in {Duration}ms | Params: {Params} | ResponseLen: {Length}",
                    toolName, sw.ElapsedMilliseconds, paramJson, result?.Length ?? 0);
        }
    }

    internal static async Task<string> LogToolCallAsync(ILogger logger, string toolName,
        Dictionary<string, object?> parameters, Func<Task<string>> execute, AppConfig config)
    {
        if (!config.Server.LogToolCalls)
            return await execute().ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        string? result = null;
        Exception? error = null;
        try
        {
            result = await execute().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            var paramJson = JsonSerializer.Serialize(parameters, Options);
            if (error is not null)
                logger.LogWarning("MCP Tool {Tool} FAILED in {Duration}ms | Params: {Params} | Error: {Error}",
                    toolName, sw.ElapsedMilliseconds, paramJson, error.Message);
            else
                logger.LogInformation("MCP Tool {Tool} OK in {Duration}ms | Params: {Params} | ResponseLen: {Length}",
                    toolName, sw.ElapsedMilliseconds, paramJson, result?.Length ?? 0);
        }
    }
}
