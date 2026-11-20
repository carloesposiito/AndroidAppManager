using AndroidAppManager.Core.Adb;

namespace AndroidAppManager.Core.Tests;

public class AdbEndpointTests
{
    [Theory]
    [InlineData("192.168.1.3", 5555, "192.168.1.3:5555")]
    [InlineData(" 10.0.0.1 ", 1, "10.0.0.1:1")]
    [InlineData("10.0.0.1", 65535, "10.0.0.1:65535")]
    public void TryCreate_valid(string host, int port, string serial)
    {
        Assert.True(AdbEndpoint.TryCreate(host, port, out var ep, out var error));
        Assert.Null(error);
        Assert.Equal(serial, ep!.Serial);
    }

    [Theory]
    [InlineData("", 5555)]
    [InlineData("   ", 5555)]
    [InlineData("192.168.1", 5555)]
    [InlineData("192.168.1.256", 5555)]
    [InlineData("not an ip", 5555)]
    [InlineData("::1", 5555)]
    [InlineData("192.168.1.3", 0)]
    [InlineData("192.168.1.3", 65536)]
    [InlineData("192.168.1.3", -1)]
    public void TryCreate_invalid(string host, int port)
    {
        Assert.False(AdbEndpoint.TryCreate(host, port, out var ep, out var error));
        Assert.Null(ep);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("192.168.1.3:5555", "192.168.1.3", 5555)]
    [InlineData("192.168.1.3", "192.168.1.3", 5555)]
    [InlineData(" 192.168.1.3 : 6000 ", "192.168.1.3", 6000)]
    public void TryParse_hostPort(string text, string host, int port)
    {
        Assert.True(AdbEndpoint.TryParse(text, out var ep, out _));
        Assert.Equal(host, ep!.Host);
        Assert.Equal(port, ep.Port);
    }

    [Theory]
    [InlineData("192.168.1.3:abc")]
    [InlineData("192.168.1.3:")]
    [InlineData("1.2.3.4:5:6")]
    [InlineData("")]
    public void TryParse_invalid(string text)
    {
        Assert.False(AdbEndpoint.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
