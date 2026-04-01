using System.Text.Json;

namespace UltimateImapMcp.Core.Coordination;

/// <summary>
/// Proxies MCP tool calls to a primary instance's HTTP API.
/// Used by secondary instances in multi-instance deployments.
/// </summary>
public sealed class ProxyToolExecutor : IToolProxy, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public ProxyToolExecutor(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<string> ExecuteAsync(string toolName, Dictionary<string, object?> parameters)
    {
        try
        {
            var url = $"{_baseUrl}/api/tools/{toolName}/execute";
            var content = new StringContent(
                JsonSerializer.Serialize(parameters, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { error = $"Primary returned {(int)response.StatusCode}: {body}" }, JsonOptions);

            return body;
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to reach primary at {_baseUrl}: {ex.Message}" }, JsonOptions);
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new { error = $"Request to primary at {_baseUrl} timed out" }, JsonOptions);
        }
    }

    public string Execute(string toolName, Dictionary<string, object?> parameters)
    {
        return ExecuteAsync(toolName, parameters).GetAwaiter().GetResult();
    }

    public void Dispose() => _httpClient.Dispose();
}
