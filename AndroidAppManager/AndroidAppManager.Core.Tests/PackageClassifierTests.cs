using AndroidAppManager.Core.Packages;

namespace AndroidAppManager.Core.Tests;

public class PackageClassifierTests
{
    [Theory]
    [InlineData("android")]
    [InlineData("com.android.systemui")]
    [InlineData("com.android.tv.settings")]
    [InlineData("com.android.providers.media")]
    [InlineData("com.android.cts.ctsshim")]
    [InlineData("android.ext.services")]
    [InlineData("org.xbmc.kodi")]
    public void Protected_packages(string pkg)
    {
        Assert.Equal(PackageRisk.Protected, PackageClassifier.Classify(pkg).Risk);
    }

    [Fact]
    public void Known_bloat_has_category()
    {
        var c = PackageClassifier.Classify("com.netflix.mediaclient");
        Assert.Equal(PackageRisk.Known, c.Risk);
        Assert.False(string.IsNullOrEmpty(c.Category));
    }

    [Fact]
    public void Protected_beats_known_and_suspect()
    {
        // "com.android.camera2" is known bloat; "com.android.settings" matches no bloat but must stay protected.
        Assert.Equal(PackageRisk.Protected, PackageClassifier.Classify("com.android.settings").Risk);
        Assert.Equal(PackageRisk.Known, PackageClassifier.Classify("com.android.camera2").Risk);
    }

    [Theory]
    [InlineData("com.foo.hulu.tv")]
    [InlineData("com.vendor.factorytest")]
    [InlineData("com.some.LAUNCHER")]
    public void Suspect_by_keyword_case_insensitive(string pkg)
    {
        Assert.Equal(PackageRisk.Suspect, PackageClassifier.Classify(pkg).Risk);
    }

    [Fact]
    public void Unknown_otherwise()
    {
        var c = PackageClassifier.Classify("com.acme.widget");
        Assert.Equal(PackageRisk.Unknown, c.Risk);
        Assert.Null(c.Category);
    }

    [Fact]
    public void Empty_name_is_unknown_not_crash()
    {
        Assert.Equal(PackageRisk.Unknown, PackageClassifier.Classify("").Risk);
    }
}
