using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.Core;

/// <summary>
/// Utilities for TCP port availability checks and standby-until-free waiting.
/// </summary>
public static class PortUtils
{
    /// <summary>Returns true if a TCP listener is already bound to the given port on loopback.</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch (SocketException ex)
        {
            // Permission denied or other errors — rethrow so callers get a clear error
            // instead of entering an infinite standby loop
            throw new InvalidOperationException(
                $"Cannot bind to port {port}: {ex.SocketErrorCode} — {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Waits until the given port becomes available using exponential backoff.
    /// Logs the first "port in use" at Warning level, subsequent polls at Debug level.
    /// Returns immediately if the port is already free.
    /// </summary>
    public static async Task WaitForPortWithBackoffAsync(
        int port, ILogger logger, string serviceName,
        CancellationToken cancellationToken,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null)
    {
        if (!IsPortInUse(port))
            return;

        var delay = initialDelay ?? TimeSpan.FromSeconds(2);
        var cap = maxDelay ?? TimeSpan.FromSeconds(60);

        logger.LogWarning(
            "{Service}: port {Port} is in use (another instance may be running). " +
            "Running in standby — will take over when the port is released",
            serviceName, port);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (!IsPortInUse(port))
            {
                logger.LogInformation(
                    "{Service}: port {Port} is now available — taking over",
                    serviceName, port);
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, cap.Ticks));

            logger.LogDebug(
                "{Service}: port {Port} still in use — next check in {Delay}s",
                serviceName, port, delay.TotalSeconds);
        }
    }
}
