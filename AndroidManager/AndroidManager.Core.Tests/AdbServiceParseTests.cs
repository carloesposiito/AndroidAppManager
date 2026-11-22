using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Tests;

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
    public void ParseProps_reads_battery_and_storage()
    {
        var i = AdbService.ParseProps("s",
            "Google\nPixel 5\n13\n33\narm64-v8a\nlevel: 85\n/dev/block/dm-9 114G 40G 74G 36% /data\n");
        Assert.Equal("Pixel 5", i.Model);
        Assert.Equal("arm64-v8a", i.Abi);
        Assert.Equal(85, i.BatteryPercent);
        Assert.Equal("40G / 114G", i.Storage);
    }

    [Fact]
    public void ParseProps_extras_missing_or_odd_keep_defaults_and_do_not_shift_props()
    {
        var i = AdbService.ParseProps("s", "G\nM\n13\n33\narm\n/dev/x 5G 1G 4G 20% /data\n");
        Assert.Equal("arm", i.Abi);
        Assert.Equal(-1, i.BatteryPercent);
        Assert.Equal("1G / 5G", i.Storage);
        Assert.Equal(-1, AdbService.ParseProps("s", "G\nM\n13\n33\narm\nlevel: abc\n").BatteryPercent);
        Assert.Equal("", AdbService.ParseProps("s", "G\nM\n13\n33\narm\n").Storage);
    }

    [Fact]
    public async Task Pair_rejects_non_numeric_code()
    {
        AdbEndpoint.TryCreate("1.2.3.4", 37000, out var ep, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbService().PairAsync(ep!, "12ab"));
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbService().PairAsync(ep!, " "));
    }
}
