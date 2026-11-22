using AndroidManager.Core.Packages;

namespace AndroidManager.Core.Tests;

public class PackageParserTests
{
    [Fact]
    public void ParseList_extracts_sorted_distinct_names()
    {
        var names = PackageParser.ParseList("package:com.b\r\npackage:com.a\r\npackage:com.a\r\n\r\n");
        Assert.Equal(new[] { "com.a", "com.b" }, names);
    }

    [Fact]
    public void ParseList_ignores_noise_and_errors()
    {
        var names = PackageParser.ParseList("WARNING: linker\nError: Could not access the Package Manager\npackage:com.a\n");
        Assert.Equal(new[] { "com.a" }, names);
    }

    [Fact]
    public void ParseList_empty()
    {
        Assert.Empty(PackageParser.ParseList(""));
        Assert.Empty(PackageParser.ParseList("package:"));
    }

    [Fact]
    public void Build_merges_origin_and_disabled()
    {
        var list = PackageParser.Build(
            system: new[] { "android", "com.netflix.mediaclient" },
            thirdParty: new[] { "com.acme.widget" },
            disabled: new[] { "com.netflix.mediaclient" });

        Assert.Equal(3, list.Count);
        var n = list.Single(p => p.Name == "com.netflix.mediaclient");
        Assert.Equal(PackageOrigin.System, n.Origin);
        Assert.True(n.IsDisabled);
        Assert.Equal(PackageRisk.Known, n.Risk);
        Assert.Equal(PackageOrigin.ThirdParty, list.Single(p => p.Name == "com.acme.widget").Origin);
    }

    [Fact]
    public void Build_dedupes_package_listed_in_both_and_keeps_system()
    {
        var list = PackageParser.Build(new[] { "com.x" }, new[] { "com.x" }, Array.Empty<string>());
        Assert.Equal(PackageOrigin.System, Assert.Single(list).Origin);
    }
}
