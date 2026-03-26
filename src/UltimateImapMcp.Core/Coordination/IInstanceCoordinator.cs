namespace UltimateImapMcp.Core.Coordination;

public interface IInstanceCoordinator
{
    bool IsLeader { get; }
    string InstanceId { get; }
    IReadOnlyList<InstanceHeartbeat> GetLiveInstances();
    Task<bool> RequestShutdownAsync(string instanceId);
}
