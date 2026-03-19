// src/UltimateImapMcp.Core/Providers/ProviderProfile.cs
namespace UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Types;

public record ProviderProfile
{
    public required ProviderType Type { get; init; }
    public required string Name { get; init; }
    public required Dictionary<FolderRole, string> FolderMap { get; init; }
    public required AuthMethod[] SupportedAuth { get; init; }
    public required SearchCapabilities Search { get; init; }
    public int MaxConnections { get; init; } = 3;
    public bool RequiresTlsTrust { get; init; } = false;
}
