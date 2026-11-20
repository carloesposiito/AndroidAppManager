using AndroidAppManager.Core.Apk;

namespace AndroidAppManager.Core.Tests;

public class ApkValidatorTests
{
    private static ApkInfo Apk(int? minSdk = 21, int? targetSdk = 33, long ver = 10, string[]? abis = null,
        bool v1 = true, bool v2 = false, bool launcher = true, bool leanback = true, long size = 10_000_000) =>
        new("a.apk", size, new string('0', 64),
            new ApkManifest("com.x.y", ver, "1.0", minSdk, targetSdk, launcher, leanback, false),
            abis ?? [], new ApkSignature(v1, v2));

    private static DeviceFacts Dev(int sdk = 29, string[]? abis = null, long free = 5_000_000_000, long? installed = null) =>
        new(sdk, abis ?? ["arm64-v8a", "armeabi-v7a"], free, installed, installed is not null);

    [Fact]
    public void Installed_with_unreadable_version_warns_instead_of_claiming_fresh_install()
    {
        var f = Run(Apk(), new DeviceFacts(29, ["arm64-v8a"], 5_000_000_000, null, IsInstalled: true));
        Assert.Contains(f, x => x.Code == FindingCode.InstalledUnknownVersion && x.Severity == FindingSeverity.Warning);
        Assert.DoesNotContain(f, x => x.Code == FindingCode.FreshInstall);
        Assert.DoesNotContain(f, x => x.Severity == FindingSeverity.Error);
    }

    private static IReadOnlyList<Finding> Run(ApkInfo a, DeviceFacts d) => ApkValidator.Validate(a, d);
    private static bool Has(IReadOnlyList<Finding> f, FindingCode c, FindingSeverity s) => f.Any(x => x.Code == c && x.Severity == s);

    [Fact]
    public void Clean_install_has_no_errors()
    {
        var f = Run(Apk(), Dev());
        Assert.DoesNotContain(f, x => x.Severity == FindingSeverity.Error);
        Assert.Contains(f, x => x.Code == FindingCode.FreshInstall);
    }

    [Fact]
    public void Unsigned_is_error() =>
        Assert.True(Has(Run(Apk(v1: false, v2: false), Dev()), FindingCode.NotSigned, FindingSeverity.Error));

    [Fact]
    public void V2_only_signature_is_accepted() =>
        Assert.DoesNotContain(Run(Apk(v1: false, v2: true), Dev()), x => x.Code == FindingCode.NotSigned);

    [Theory]
    [InlineData(30, 29, true)]
    [InlineData(29, 29, false)]   // boundary: equal is fine
    [InlineData(21, 29, false)]
    public void MinSdk_boundary(int min, int dev, bool error) =>
        Assert.Equal(error, Has(Run(Apk(minSdk: min), Dev(sdk: dev)), FindingCode.MinSdkTooHigh, FindingSeverity.Error));

    [Fact]
    public void Missing_minSdk_is_not_an_error() =>
        Assert.DoesNotContain(Run(Apk(minSdk: null), Dev()), x => x.Code == FindingCode.MinSdkTooHigh);

    [Theory]
    [InlineData(34, 22, true)]
    [InlineData(34, 23, false)]
    [InlineData(33, 22, false)]   // rule only from SDK 34
    public void Deprecated_target_sdk(int devSdk, int target, bool error) =>
        Assert.Equal(error, Has(Run(Apk(targetSdk: target), Dev(sdk: devSdk)), FindingCode.DeprecatedTargetSdk, FindingSeverity.Error));

    [Fact]
    public void Abi_mismatch_is_error_and_universal_is_ok()
    {
        Assert.True(Has(Run(Apk(abis: ["x86_64"]), Dev()), FindingCode.AbiMismatch, FindingSeverity.Error));
        Assert.False(Has(Run(Apk(abis: ["arm64-v8a"]), Dev()), FindingCode.AbiMismatch, FindingSeverity.Error));
        Assert.False(Has(Run(Apk(abis: []), Dev()), FindingCode.AbiMismatch, FindingSeverity.Error)); // pure Java
        Assert.False(Has(Run(Apk(abis: ["x86", "armeabi-v7a"]), Dev()), FindingCode.AbiMismatch, FindingSeverity.Error));
    }

    [Fact]
    public void Space_check_uses_double_size_and_ignores_unknown()
    {
        Assert.True(Has(Run(Apk(size: 100), Dev(free: 150)), FindingCode.NoSpace, FindingSeverity.Error));
        Assert.False(Has(Run(Apk(size: 100), Dev(free: 200)), FindingCode.NoSpace, FindingSeverity.Error));
        Assert.False(Has(Run(Apk(size: 100), Dev(free: -1)), FindingCode.NoSpace, FindingSeverity.Error)); // df unreadable
    }

    [Fact]
    public void Version_relations()
    {
        Assert.True(Has(Run(Apk(ver: 5), Dev(installed: 9)), FindingCode.Downgrade, FindingSeverity.Error));
        Assert.True(Has(Run(Apk(ver: 9), Dev(installed: 9)), FindingCode.Reinstall, FindingSeverity.Warning));
        Assert.True(Has(Run(Apk(ver: 10), Dev(installed: 9)), FindingCode.Upgrade, FindingSeverity.Info));
    }

    [Fact]
    public void Launcher_warnings()
    {
        Assert.DoesNotContain(Run(Apk(leanback: true), Dev()), x => x.Severity == FindingSeverity.Warning);
        Assert.True(Has(Run(Apk(leanback: false, launcher: true), Dev()), FindingCode.PhoneOnlyLauncher, FindingSeverity.Warning));
        Assert.True(Has(Run(Apk(leanback: false, launcher: false), Dev()), FindingCode.NoLeanbackLauncher, FindingSeverity.Warning));
    }

    [Fact]
    public void Report_flags()
    {
        var down = new ApkCheckReport(Apk(ver: 1), Dev(installed: 2), Run(Apk(ver: 1), Dev(installed: 2)));
        Assert.True(down.HasErrors);
        Assert.True(down.OnlyDowngradeBlocks);

        var both = Run(Apk(ver: 1, v1: false), Dev(installed: 2));
        Assert.False(new ApkCheckReport(Apk(), Dev(), both).OnlyDowngradeBlocks);

        var ok = new ApkCheckReport(Apk(), Dev(), Run(Apk(), Dev()));
        Assert.False(ok.HasErrors);
        Assert.False(ok.OnlyDowngradeBlocks);
    }
}
