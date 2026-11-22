using AndroidManager.Core.Mirror;

namespace AndroidManager.Core.Tests;

public class ScreenGeometryTests
{
    [Fact]
    public void Parse_reads_physical_size_portrait()
    {
        Assert.Equal((1080, 2400), ScreenGeometry.Parse("Physical size: 1080x2400\nmCurrentOrientation=0\n"));
    }

    [Fact]
    public void Parse_prefers_override_size()
    {
        Assert.Equal((1080, 2340), ScreenGeometry.Parse("Physical size: 1080x2400\nOverride size: 1080x2340\n"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Parse_swaps_for_landscape(int orientation)
    {
        Assert.Equal((2400, 1080), ScreenGeometry.Parse($"Physical size: 1080x2400\r\n  mCurrentOrientation={orientation}\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Physical size: axb")]
    [InlineData("Physical size: 0x0")]
    [InlineData("error: closed")]
    public void Parse_returns_null_for_garbage(string output) => Assert.Null(ScreenGeometry.Parse(output));

    [Theory]
    [InlineData(800, 600, 1080, 2400, 270, 600)]     // tall phone in a wide panel: height limits
    [InlineData(300, 900, 1080, 2400, 300, 666.67)]  // narrow panel: width limits
    [InlineData(500, 500, 100, 100, 500, 500)]
    public void Contain_fits_inside_keeping_the_ratio(double aw, double ah, int w, int h, double ew, double eh)
    {
        var (cw, ch) = ScreenGeometry.Contain(aw, ah, w, h);
        Assert.Equal(ew, cw, 1);
        Assert.Equal(eh, ch, 1);
        Assert.True(cw <= aw + 0.01 && ch <= ah + 0.01);
    }

    [Fact]
    public void Contain_with_no_room_returns_zero()
    {
        Assert.Equal((0d, 0d), ScreenGeometry.Contain(0, 100, 10, 20));
        Assert.Equal((0d, 0d), ScreenGeometry.Contain(100, 100, 0, 20));
    }
}
