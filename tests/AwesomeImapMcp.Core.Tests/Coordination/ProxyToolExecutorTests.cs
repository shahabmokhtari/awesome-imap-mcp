using AwesomeImapMcp.Core.Coordination;

namespace AwesomeImapMcp.Core.Tests.Coordination;

public class ProxyToolExecutorTests
{
    [Fact]
    public void Constructor_SetsBaseUrl()
    {
        var proxy = new ProxyToolExecutor("http://localhost:3846");
        Assert.NotNull(proxy);
        proxy.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_UnreachableHost_ReturnsError()
    {
        var proxy = new ProxyToolExecutor("http://localhost:19999");
        var result = await proxy.ExecuteAsync("test_tool", new Dictionary<string, object?>());
        Assert.Contains("error", result);
        proxy.Dispose();
    }

    [Fact]
    public void Execute_UnreachableHost_ReturnsError()
    {
        var proxy = new ProxyToolExecutor("http://localhost:19999");
        var result = proxy.Execute("test_tool", new Dictionary<string, object?>());
        Assert.Contains("error", result);
        proxy.Dispose();
    }
}
