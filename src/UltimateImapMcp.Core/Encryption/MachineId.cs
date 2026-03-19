using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UltimateImapMcp.Core.Encryption;

/// <summary>
/// Provides a stable, machine-unique identifier for use as an encryption passphrase.
/// Returns a SHA-256 hex hash of the platform's hardware/OS identifier.
/// </summary>
public static class MachineId
{
    private static readonly Lazy<string> _cached = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Returns the SHA-256 hex hash of this machine's unique identifier.</summary>
    public static string Get() => _cached.Value;

    private static string Compute()
    {
        var raw = GetRawIdentifier();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetRawIdentifier()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWindowsMachineGuid();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetMacOsPlatformUuid();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetLinuxMachineId();
        }
        catch
        {
            // fall through to fallback
        }

        return Environment.MachineName;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography");
        var value = key?.GetValue("MachineGuid")?.ToString();
        return string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value;
    }

    private static string GetMacOsPlatformUuid()
    {
        var output = RunProcess("ioreg", "-rd1 -c IOPlatformExpertDevice");
        var match = Regex.Match(output, @"""IOPlatformUUID""\s*=\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : Environment.MachineName;
    }

    private static string GetLinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }
        }
        return Environment.MachineName;
    }

    private static string RunProcess(string fileName, string arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
