using AndroidAppManager.Core.Adb;

namespace AndroidAppManager.Core.Tests;

public class AdbServiceParseTests
{
    [Fact]
    public void ParseProps_full()
    {
        var i = AdbService.ParseProps("1.2.3.4:5555", "rockchip\r\nR2 Plus\r\n10\r\n29\r\narm64-v8a\r\n");
        Assert.Equal(new DeviceInfo("1.2.3.4:5555", "rockchip", "R2 Plus", "10", 29, "arm64-v8a"), i);
    }

    [Fact]
    public void ParseProps_partial_and_empty_fill_placeholders()
    {
        var i = AdbService.ParseProps("s", "only\n");
        Assert.Equal("only", i.Manufacturer);
        Assert.Equal("?", i.Model);
        Assert.Equal(0, i.Sdk);
        Assert.Equal("?", AdbService.ParseProps("s", "").Abi);
    }

    [Fact]
    public async Task Pair_rejects_non_numeric_code()
    {
        AdbEndpoint.TryCreate("1.2.3.4", 37000, out var ep, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbService().PairAsync(ep!, "12ab"));
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbService().PairAsync(ep!, " "));
    }
}
