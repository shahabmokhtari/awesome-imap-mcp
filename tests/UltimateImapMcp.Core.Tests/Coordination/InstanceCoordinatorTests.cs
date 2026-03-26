using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Tests.Coordination;

public class InstanceCoordinatorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    private static string FreshTime() => DateTime.UtcNow.ToString("o");
    private static string StaleTime() => DateTime.UtcNow.AddMinutes(-10).ToString("o");

    [Fact]
    public void Dashboard_host_wins()
    {
        var now = FreshTime();
        var records = new List<HeartbeatRecord>
        {
            new("inst-plain", 1, "/", "stdio", false, now, now, 0, 0, 0, false),
            new("inst-dash", 2, "/", "stdio", true, now, now, 0, 0, 0, false),
        };

        var leader = InstanceCoordinator.ComputeLeaderId(records, Threshold);
        Assert.Equal("inst-dash", leader);
    }

    [Fact]
    public void Oldest_wins_when_no_dashboard()
    {
        var older = DateTime.UtcNow.AddMinutes(-2).ToString("o");
        var newer = FreshTime();

        var records = new List<HeartbeatRecord>
        {
            new("inst-new", 2, "/", "stdio", false, newer, newer, 0, 0, 0, false),
            new("inst-old", 1, "/", "stdio", false, older, older, 0, 0, 0, false),
        };

        var leader = InstanceCoordinator.ComputeLeaderId(records, Threshold);
        Assert.Equal("inst-old", leader);
    }

    [Fact]
    public void Stale_excluded()
    {
        var stale = StaleTime();
        var fresh = FreshTime();

        var records = new List<HeartbeatRecord>
        {
            new("inst-stale", 1, "/", "stdio", true, stale, stale, 0, 0, 0, false),
            new("inst-fresh", 2, "/", "stdio", false, fresh, fresh, 0, 0, 0, false),
        };

        var leader = InstanceCoordinator.ComputeLeaderId(records, Threshold);
        Assert.Equal("inst-fresh", leader);
    }

    [Fact]
    public void Multiple_dashboards_oldest_wins()
    {
        var older = DateTime.UtcNow.AddMinutes(-2).ToString("o");
        var newer = FreshTime();

        var records = new List<HeartbeatRecord>
        {
            new("dash-new", 2, "/", "stdio", true, newer, newer, 0, 0, 0, false),
            new("dash-old", 1, "/", "stdio", true, older, older, 0, 0, 0, false),
        };

        var leader = InstanceCoordinator.ComputeLeaderId(records, Threshold);
        Assert.Equal("dash-old", leader);
    }

    [Fact]
    public void Returns_null_empty()
    {
        var leader = InstanceCoordinator.ComputeLeaderId(new List<HeartbeatRecord>(), Threshold);
        Assert.Null(leader);
    }
}
