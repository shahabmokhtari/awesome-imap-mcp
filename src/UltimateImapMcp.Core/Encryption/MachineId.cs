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
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[MachineId] Failed to get platform-specific machine identifier ({ex.GetType().Name}: {ex.Message}). "
                + "Falling back to MachineName. Credential encryption keys may differ if this changes.");
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
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return Environment.MachineName;

            // Read async to avoid deadlock when stdout buffer fills
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(TimeSpan.FromSeconds(5)))
            {
                try { process.Kill(); } catch (Exception ex) { Console.Error.WriteLine($"[MachineId] Failed to kill process: {ex.Message}"); }
                return Environment.MachineName;
            }
            process.WaitForExit(1000); // brief wait for clean exit
            return readTask.Result.Trim();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MachineId] RunProcess failed: {ex.Message}");
            return Environment.MachineName;
        }
    }
}
