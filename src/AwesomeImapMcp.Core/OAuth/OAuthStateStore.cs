using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AwesomeImapMcp.Core.OAuth;

/// <summary>
/// Thread-safe in-memory store for pending OAuth authorization flows.
/// Entries auto-expire after 10 minutes.
/// </summary>
public sealed class OAuthStateStore
{
    private readonly ConcurrentDictionary<string, OAuthPendingFlow> _flows = new();
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Creates a new pending flow entry and returns the state key.
    /// </summary>
    public string Create(OAuthPendingFlow flow)
    {
        PurgeExpired();

        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Base64UrlEncode(stateBytes);

        flow = flow with { CreatedAt = DateTime.UtcNow };
        _flows[state] = flow;

        return state;
    }

    /// <summary>
    /// Retrieves and removes the pending flow for the given state.
    /// Returns null if not found or expired.
    /// </summary>
    public OAuthPendingFlow? TryConsume(string state)
    {
        if (!_flows.TryRemove(state, out var flow))
            return null;

        if (DateTime.UtcNow - flow.CreatedAt > Expiry)
            return null;

        return flow;
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var (key, flow) in _flows)
        {
            if (flow.CreatedAt < cutoff)
                _flows.TryRemove(key, out _);
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>
/// Represents a pending OAuth authorization flow awaiting callback.
/// </summary>
public record OAuthPendingFlow
{
    public required string Provider { get; init; }
    public required string CodeVerifier { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? RedirectUri { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
