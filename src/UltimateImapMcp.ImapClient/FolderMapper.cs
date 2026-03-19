using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Types;

namespace UltimateImapMcp.ImapClient;

public class FolderMapper(ProviderProfile profile)
{
    private readonly Dictionary<string, FolderRole> _reverseMap =
        profile.FolderMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public string GetPath(FolderRole role) => profile.FolderMap[role];

    public FolderRole? DetectRole(string path)
    {
        return _reverseMap.TryGetValue(path, out var role) ? role : null;
    }

    public string GetDisplayName(string path, FolderRole? role)
    {
        if (role.HasValue) return role.Value.ToString();
        var parts = path.Split(profile.FolderMap.Values.FirstOrDefault()?.Contains('/') == true ? '/' : '.');
        return parts[^1];
    }
}
