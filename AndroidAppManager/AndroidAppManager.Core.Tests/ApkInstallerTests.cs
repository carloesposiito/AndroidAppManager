using AndroidAppManager.Core.Apk;
using AndroidAppManager.Core.Tests.Support;

namespace AndroidAppManager.Core.Tests;

public class ApkInstallerTests
{
    private static string Sample() => TestAssets.Apk(TestAssets.Launcher);

    private const string Pkg = "se.blunden.xbmclauncher";

    /// <param name="pmOut">`pm list packages --show-versioncode` output.</param>
    /// <param name="plainOut">`pm list packages` (no flag) output, used when the flag is unsupported.</param>
    /// <param name="dumpsys">`dumpsys package` output.</param>
    private static FakeAdb Device(string pmOut = "", int sdk = 29, string plainOut = "", string dumpsys = "",
        string df = "Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/x 100 10 54512272 2% /data\n") =>
        new(c => c switch
        {
            var p when p.StartsWith("getprop") => $"{sdk}\narm64-v8a,armeabi-v7a\narm64-v8a\n",
            var d when d.StartsWith("df") => df,
            var l when l.StartsWith("pm list packages --show-versioncode") => pmOut,
            var l when l.StartsWith("pm list packages") => plainOut,
            var l when l.StartsWith("dumpsys package") => dumpsys,
            _ => ""
        });

    [Theory]
    [InlineData("package:se.blunden.xbmclauncher versionCode:7\n", Pkg, true, 7L)]
    [InlineData("package:se.blunden.xbmclauncher.pro versionCode:9\npackage:se.blunden.xbmclauncher versionCode:7\n", Pkg, true, 7L)]
    [InlineData("package:se.blunden.xbmclauncher.pro versionCode:9\n", Pkg, false, null)] // substring match must not leak
    [InlineData("package:se.blunden.xbmclauncher\n", Pkg, true, null)]                    // installed, no version column
    [InlineData("package:se.blunden.xbmclauncher\r\n", Pkg, true, null)]
    [InlineData("", Pkg, false, null)]
    [InlineData("Error: unknown option --show-versioncode", Pkg, false, null)]
    public void ParseInstalled(string output, string pkg, bool installed, long? version) =>
        Assert.Equal((installed, version), ApkInstaller.ParseInstalled(output, pkg));

    [Fact]
    public void ParseDumpsysVersion()
    {
        Assert.Equal(29, ApkInstaller.ParseDumpsysVersion("  userId=10123\n  versionCode=29 minSdk=14 targetSdk=34\n"));
        Assert.Null(ApkInstaller.ParseDumpsysVersion("Unable to find package"));
    }

    [Fact]
    public void ParseFacts_reads_abilist_and_falls_back_to_abi()
    {
        var f = ApkInstaller.ParseFacts("29\narm64-v8a,armeabi-v7a\narm64-v8a\n", "x\n/dev 1 1 100 1% /data", 3, true);
        Assert.Equal(29, f.Sdk);
        Assert.Equal(new[] { "arm64-v8a", "armeabi-v7a" }, f.Abis);
        Assert.Equal(102_400, f.FreeBytes);
        Assert.Equal(3, f.InstalledVersionCode);
        Assert.True(f.IsInstalled);
        Assert.Equal(new[] { "armeabi-v7a" }, ApkInstaller.ParseFacts("21\n\narmeabi-v7a\n", "", null, false).Abis);
    }

    [Fact]
    public async Task Old_pm_without_versioncode_flag_falls_back_to_dumpsys_for_downgrade_check()
    {
        var p = Sample();
        var probe = await new ApkInstaller(Device()).CheckAsync(FakeAdb.Ep(), p);
        var newer = probe.Apk.Manifest.VersionCode + 5;
        var adb = Device(pmOut: "", plainOut: $"package:{Pkg}\n", dumpsys: $"  versionCode={newer} minSdk=14\n");
        var report = await new ApkInstaller(adb).CheckAsync(FakeAdb.Ep(), p);
        Assert.True(report.Device.IsInstalled);
        Assert.Equal(newer, report.Device.InstalledVersionCode);
        Assert.True(report.OnlyDowngradeBlocks);
    }

    [Fact]
    public async Task Version_unreadable_warns_and_a_successful_install_is_not_reported_as_failure()
    {
        var p = Sample();
        var adb = Device(pmOut: "", plainOut: $"package:{Pkg}\n", dumpsys: "");
        var inst = new ApkInstaller(adb);
        var report = await inst.CheckAsync(FakeAdb.Ep(), p);
        Assert.Contains(report.Findings, f => f.Code == FindingCode.InstalledUnknownVersion && f.Severity == FindingSeverity.Warning);
        Assert.False(report.HasErrors);

        var res = await inst.InstallAsync(FakeAdb.Ep(), report, false);
        Assert.True(res.Success, res.Message);
        Assert.Contains("non verificabile", res.Message);
    }

    [Fact]
    public async Task Success_reply_but_package_absent_is_failure()
    {
        var p = Sample();
        var adb = Device(); // nothing installed before or after
        var report = await new ApkInstaller(adb).CheckAsync(FakeAdb.Ep(), p);
        var res = await new ApkInstaller(adb).InstallAsync(FakeAdb.Ep(), report, false);
        Assert.False(res.Success);
        Assert.Contains("non risulta", res.Message);
    }

    [Theory]
    [InlineData("", -1)]
    [InlineData("Filesystem 1K-blocks Used Available Use% Mounted on\n", -1)]
    [InlineData("garbage", -1)]
    public void ParseFreeBytes_unreadable(string df, long expected) =>
        Assert.Equal(expected, ApkInstaller.ParseFreeBytes(df));

    [Fact]
    public void Explain_maps_known_codes_and_unknown()
    {
        Assert.Contains("Firma diversa", ApkInstaller.Explain("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: x]"));
        Assert.Contains("Spazio", ApkInstaller.Explain("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]"));
        Assert.Contains("Failure [WHAT]", ApkInstaller.Explain("Failure [WHAT]"));
        Assert.Contains("senza messaggio", ApkInstaller.Explain("  "));
    }

    [Fact]
    public async Task Check_then_install_verifies_version()
    {
        var p = Sample();
        var adb = Device();
        var inst = new ApkInstaller(adb);
        var report = await inst.CheckAsync(FakeAdb.Ep(), p);
        Assert.False(report.HasErrors);
        Assert.Empty(adb.Installs); // check never installs

        // device now reports the APK's version
        var adb2 = Device(pmOut: $"package:{Pkg} versionCode:{report.Apk.Manifest.VersionCode}\n");
        var res = await new ApkInstaller(adb2).InstallAsync(FakeAdb.Ep(), report, false);
        Assert.True(res.Success, res.Message);
        Assert.Single(adb2.Installs);
    }

    [Fact]
    public async Task Install_success_but_version_differs_is_failure()
    {
        var p = Sample();
        var report = await new ApkInstaller(Device()).CheckAsync(FakeAdb.Ep(), p);
        var stale = Device(pmOut: $"package:{Pkg} versionCode:{report.Apk.Manifest.VersionCode - 1}\n");
        var res = await new ApkInstaller(stale).InstallAsync(FakeAdb.Ep(), report, false);
        Assert.False(res.Success);
        Assert.Contains("attesa", res.Message);
    }

    [Fact]
    public async Task Blocked_report_never_reaches_adb_install()
    {
        var p = Sample();
        var adb = Device();
        var report = await new ApkInstaller(adb).CheckAsync(FakeAdb.Ep(), p);
        var blocked = report with { Findings = [new Finding(FindingSeverity.Error, FindingCode.NotSigned, "x")] };
        var res = await new ApkInstaller(adb).InstallAsync(FakeAdb.Ep(), blocked, allowDowngrade: true);
        Assert.False(res.Success);
        Assert.Empty(adb.Installs);
    }

    [Fact]
    public async Task Downgrade_needs_explicit_opt_in()
    {
        var p = Sample();
        var probe = await new ApkInstaller(Device()).CheckAsync(FakeAdb.Ep(), p);
        var newer = probe.Apk.Manifest.VersionCode + 5;
        var adb = Device(pmOut: $"package:{Pkg} versionCode:{newer}\n");
        var inst = new ApkInstaller(adb);
        var report = await inst.CheckAsync(FakeAdb.Ep(), p);
        Assert.True(report.OnlyDowngradeBlocks);

        var denied = await inst.InstallAsync(FakeAdb.Ep(), report, allowDowngrade: false);
        Assert.False(denied.Success);
        Assert.Empty(adb.Installs);

        await inst.InstallAsync(FakeAdb.Ep(), report, allowDowngrade: true);
        Assert.True(Assert.Single(adb.Installs).Downgrade);
    }

    [Fact]
    public async Task Install_failure_is_explained()
    {
        var p = Sample();
        var adb = Device();
        adb.InstallOutput = "Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Package signatures do not match]";
        var report = await new ApkInstaller(adb).CheckAsync(FakeAdb.Ep(), p);
        var res = await new ApkInstaller(adb).InstallAsync(FakeAdb.Ep(), report, false);
        Assert.False(res.Success);
        Assert.Contains("Firma diversa", res.Message);
    }

    [Fact]
    public async Task Check_rejects_missing_file_without_touching_device()
    {
        var adb = Device();
        await Assert.ThrowsAsync<InvalidApkException>(() => new ApkInstaller(adb).CheckAsync(FakeAdb.Ep(), "C:\\nope.apk"));
        Assert.Empty(adb.Commands);
    }
}
