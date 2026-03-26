namespace UltimateImapMcp.Core.Coordination;

public record InstanceHeartbeat(
    string InstanceId, int ProcessId, string Cwd, string Transport,
    bool IsDashboardHost, bool IsLeader, string StartedAt,
    string LastHeartbeat, int AccountsCount, long CpuTimeMs,
    int MemoryMb, bool ShutdownRequested);
