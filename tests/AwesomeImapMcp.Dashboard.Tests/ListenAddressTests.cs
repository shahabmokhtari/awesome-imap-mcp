using System.Net;
using AwesomeImapMcp.Dashboard;

namespace AwesomeImapMcp.Dashboard.Tests;

public class ListenAddressTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("loopback")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void Resolve_LoopbackForms_StayOffTheNetwork(string? value)
    {
        Assert.Null(ListenAddress.Resolve(value));
        Assert.True(ListenAddress.IsLoopbackOnly(value));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("+")]
    public void Resolve_Wildcards_BindEveryInterface(string value)
    {
        Assert.Equal(IPAddress.Any, ListenAddress.Resolve(value));
        Assert.False(ListenAddress.IsLoopbackOnly(value));
    }

    [Fact]
    public void Resolve_SpecificLanAddress_IsHonoured()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.20"), ListenAddress.Resolve("192.168.1.20"));
        Assert.False(ListenAddress.IsLoopbackOnly("192.168.1.20"));
    }

    [Fact]
    public void Resolve_Garbage_FailsLoudlyInsteadOfOpeningUp()
    {
        // A typo must never silently fall back to a wider binding.
        Assert.Throws<ArgumentException>(() => ListenAddress.Resolve("0.0.0.O"));
    }

    [Fact]
    public void Default_ConfigValue_IsLoopback()
    {
        var config = new AwesomeImapMcp.Core.Configuration.ServerConfig();
        Assert.True(ListenAddress.IsLoopbackOnly(config.BindAddress));
    }
}
