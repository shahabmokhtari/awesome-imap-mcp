using UltimateImapMcp.McpServer.Tools;

namespace UltimateImapMcp.McpServer.Tests.Tools;

public class BodyFetchToolsTests
{
    [Fact]
    public void FetchBodies_HasExpectedParameters()
    {
        var method = typeof(BodyFetchTools).GetMethod("FetchBodies");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.Name == "accountId");
        Assert.Contains(parameters, p => p.Name == "uids");
        Assert.Contains(parameters, p => p.Name == "folder");
    }
}
