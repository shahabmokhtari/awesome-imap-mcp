namespace AwesomeImapMcp.Core;

/// <summary>
/// Identifies this server instance for log correlation.
/// Format: {MachineName}-{ProcessId}-{StartTimestamp}
/// </summary>
public record InstanceInfo(string Id);
