using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateImapMcp.Core.Configuration;

/// <summary>
/// Loads and deserialises application configuration from a JSON file,
/// performing environment-variable substitution before parsing.
/// </summary>
public static partial class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the configuration from <paramref name="path"/>.
    /// Throws <see cref="FileNotFoundException"/> if the file does not exist.
    /// </summary>
    public static AppConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}", path);

        var raw = File.ReadAllText(path);
        var substituted = SubstituteEnvVars(raw);

        var config = JsonSerializer.Deserialize<AppConfig>(substituted, JsonOptions)
                     ?? new AppConfig();

        return config;
    }

    /// <summary>
    /// Expands a leading <c>~/</c> in <paramref name="dbPath"/> to the current
    /// user's home directory.
    /// </summary>
    public static string ResolveDbPath(string dbPath)
    {
        if (dbPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, dbPath[2..]);
        }
        return dbPath;
    }

    /// <summary>
    /// Replaces every <c>${VAR_NAME}</c> token in <paramref name="input"/>
    /// with the corresponding environment variable value.  Tokens whose
    /// variable is not set are left unchanged.
    /// </summary>
    private static string SubstituteEnvVars(string input) =>
        EnvVarPattern().Replace(input, m =>
        {
            var varName = m.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName) ?? m.Value;
        });

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex EnvVarPattern();
}
