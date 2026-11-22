using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Tests;

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

    [Fact]
    public void ForSerial_usb_has_no_network_address()
    {
        var ep = AdbEndpoint.ForSerial("R58M123ABC");
        Assert.Equal("R58M123ABC", ep.Serial);
        Assert.False(ep.IsWireless);
    }

    [Fact]
    public void ForSerial_equals_by_serial_and_differs_from_wireless()
    {
        Assert.Equal(AdbEndpoint.ForSerial("A1"), AdbEndpoint.ForSerial("A1"));
        Assert.NotEqual(AdbEndpoint.ForSerial("A1"), AdbEndpoint.ForSerial("A2"));
        AdbEndpoint.TryCreate("10.0.0.1", 5555, out var wifi, out _);
        Assert.True(wifi!.IsWireless);
        Assert.NotEqual(wifi, AdbEndpoint.ForSerial("10.0.0.1:5555x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ForSerial_rejects_blank(string serial) =>
        Assert.Throws<ArgumentException>(() => AdbEndpoint.ForSerial(serial));

    [Fact]
    public void FromSerial_recognizes_wifi_serials()
    {
        var wifi = AdbEndpoint.FromSerial("192.168.1.9:5555");
        Assert.True(wifi.IsWireless);
        Assert.Equal("192.168.1.9", wifi.Host);
        Assert.Equal(5555, wifi.Port);
        Assert.False(AdbEndpoint.FromSerial("R58M123ABC").IsWireless);
        Assert.False(AdbEndpoint.FromSerial("emulator-5554").IsWireless);
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
