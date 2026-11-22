using AndroidManager.Core.Adb;
using AndroidManager.Core.Packages;
using AndroidManager.Core.Tests.Support;

namespace AndroidManager.Core.Tests;

public class PackageScannerTests
{
    [Fact]
    public async Task Scan_splits_system_third_party_and_disabled()
    {
        var adb = new FakeAdb(c => c switch
        {
            "pm list packages -s" => "package:android\npackage:com.netflix.mediaclient\n",
            "pm list packages -3" => "package:com.acme.widget\n",
            "pm list packages -d" => "package:com.netflix.mediaclient\n",
            _ => ""
        });
        var list = await new PackageScanner(adb).ScanAsync(FakeAdb.Ep());
        Assert.Equal(2, list.Count(p => p.Origin == PackageOrigin.System));
        Assert.Single(list, p => p.Origin == PackageOrigin.ThirdParty);
        Assert.True(list.Single(p => p.Name == "com.netflix.mediaclient").IsDisabled);
    }

    [Fact]
    public async Task Scan_throws_when_pm_returns_nothing()
    {
        var adb = new FakeAdb(_ => "Error: Could not access the Package Manager");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PackageScanner(adb).ScanAsync(FakeAdb.Ep()));
    }
}
