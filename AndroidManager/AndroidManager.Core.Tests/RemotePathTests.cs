using AndroidManager.Core.Files;

namespace AndroidManager.Core.Tests;

public class RemotePathTests
{
    [Theory]
    [InlineData("DCIM/Camera", "DCIM/Camera")]
    [InlineData("/DCIM/Camera/", "DCIM/Camera")]
    [InlineData("  Pictures ", "Pictures")]
    [InlineData("Foto vacanze/2024", "Foto vacanze/2024")]
    [InlineData("Città/Perù", "Città/Perù")]
    public void TryFolder_accepts_and_normalizes(string input, string expected)
    {
        Assert.True(RemotePath.TryFolder(input, out var rel));
        Assert.Equal(expected, rel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("..")]
    [InlineData("DCIM/../Android")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void TryFolder_rejects_unsafe(string input) => Assert.False(RemotePath.TryFolder(input, out _));

    [Theory]
    [InlineData("IMG_1.jpg", true)]
    [InlineData("2024/IMG 1.jpg", true)]
    [InlineData("../x.jpg", false)]
    [InlineData("a/../x.jpg", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("", false)]
    [InlineData("a\nb.jpg", false)]
    public void IsSafeRelativeFile(string path, bool ok) => Assert.Equal(ok, RemotePath.IsSafeRelativeFile(path));

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a b;rm -rf /", "'a b;rm -rf /'")]
    [InlineData("$(x)`y`", "'$(x)`y`'")]
    public void Quote_wraps_in_single_quotes(string raw, string quoted) => Assert.Equal(quoted, RemotePath.Quote(raw));

    [Fact]
    public void Absolute_joins_under_sdcard()
    {
        Assert.Equal("/sdcard/DCIM/Camera/a.jpg", RemotePath.Absolute("DCIM/Camera", "a.jpg"));
        Assert.Equal("/sdcard/DCIM/Camera", RemotePath.Absolute("DCIM/Camera"));
    }

    [Theory]
    [InlineData(".thumbnails/1.jpg", true)]
    [InlineData("a/.trashed-123-x.jpg", true)]
    [InlineData(".pending-1-x.jpg", true)]
    [InlineData("a/b.jpg", false)]
    public void IsHidden(string rel, bool hidden) => Assert.Equal(hidden, RemotePath.IsHidden(rel));

    [Fact]
    public void DropNested_keeps_only_outermost_folders()
    {
        var kept = RemotePath.DropNested(["DCIM/Camera", "DCIM", "Pictures/Shots", "DCIMX", "DCIM"]);
        Assert.Equal(["DCIM", "DCIMX", "Pictures/Shots"], kept.Order().ToArray());
    }
}
