using System.IO.Compression;
using AndroidManager.Core.Apk;
using AndroidManager.Core.Tests.Support;

namespace AndroidManager.Core.Tests;

public class ApkInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("apk").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static string Sample(string name) => TestAssets.Apk(name);

    [Fact]
    public async Task Reads_real_launcher_apk()
    {
        var p = Sample("xbmc-launcher-3-4-3.apk");
        var info = await ApkInspector.InspectAsync(p);
        Assert.Equal("se.blunden.xbmclauncher", info.Manifest.Package);
        Assert.True(info.Manifest.VersionCode > 0);
        Assert.True(info.Manifest.MinSdk > 0);
        Assert.True(info.Signature.IsSigned);
        Assert.Equal(64, info.Sha256.Length);
    }

    [Fact]
    public async Task Reads_real_kodi_apk_abis_and_leanback()
    {
        var p = Sample("kodi-21.3-Omega-arm64-v8a.apk");
        var info = await ApkInspector.InspectAsync(p);
        Assert.Equal("org.xbmc.kodi", info.Manifest.Package);
        Assert.Equal(new[] { "arm64-v8a" }, info.Abis);
        Assert.True(info.Manifest.HasLeanbackLauncher);
        Assert.True(info.Signature.IsSigned);
    }

    [Theory]
    [InlineData(TestAssets.Launcher, "se.blunden.xbmclauncher", 29L, "3.4.3", 14, 34, false, new string[0])]
    [InlineData(TestAssets.Kodi, "org.xbmc.kodi", 2103000L, "21.3", 21, 34, true, new[] { "arm64-v8a" })]
    [InlineData(TestAssets.TheWorld, "com.nathnetwork.theworld", 910L, "7.0", 21, 34, true,
        new[] { "arm64-v8a", "armeabi-v7a", "x86", "x86_64" })]
    public async Task Real_apks_parse_to_known_values(string file, string pkg, long vc, string vn, int min, int tgt,
        bool leanback, string[] abis)
    {
        var i = await ApkInspector.InspectAsync(TestAssets.Apk(file));
        Assert.Equal(pkg, i.Manifest.Package);
        Assert.Equal(vc, i.Manifest.VersionCode);
        Assert.Equal(vn, i.Manifest.VersionName);
        Assert.Equal(min, i.Manifest.MinSdk);
        Assert.Equal(tgt, i.Manifest.TargetSdk);
        Assert.Equal(leanback, i.Manifest.HasLeanbackLauncher);
        Assert.Equal(abis, i.Abis);
        Assert.True(i.Signature.V1);
        Assert.True(i.Signature.V2OrV3);
    }

    [Fact]
    public async Task Missing_file_throws()
    {
        await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(Path.Combine(_dir, "nope.apk")));
    }

    [Theory]
    [InlineData(".xapk")]
    [InlineData(".apks")]
    [InlineData(".apkm")]
    public async Task Bundles_are_rejected_with_clear_message(string ext)
    {
        var f = Path.Combine(_dir, "x" + ext);
        File.WriteAllBytes(f, [1, 2, 3, 4, 5]);
        var ex = await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
        Assert.Contains("bundle", ex.Message);
    }

    [Fact]
    public async Task Garbage_file_is_invalid_not_crash()
    {
        var f = Path.Combine(_dir, "g.apk");
        File.WriteAllBytes(f, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray());
        await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
    }

    [Fact]
    public async Task Empty_and_tiny_files_are_invalid()
    {
        var f = Path.Combine(_dir, "e.apk");
        File.WriteAllBytes(f, []);
        await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
    }

    [Fact]
    public async Task Zip_without_manifest_is_invalid()
    {
        var f = Path.Combine(_dir, "z.apk");
        using (var z = ZipFile.Open(f, ZipArchiveMode.Create)) z.CreateEntry("hello.txt");
        var ex = await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
        Assert.Contains("AndroidManifest", ex.Message);
    }

    [Fact]
    public async Task Zip_with_inner_apk_reports_bundle()
    {
        var f = Path.Combine(_dir, "b.apk");
        using (var z = ZipFile.Open(f, ZipArchiveMode.Create)) z.CreateEntry("base.apk");
        var ex = await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
        Assert.Contains("bundle", ex.Message);
    }

    [Fact]
    public async Task Truncated_real_apk_is_invalid()
    {
        var p = Sample("xbmc-launcher-3-4-3.apk");
        var f = Path.Combine(_dir, "t.apk");
        var bytes = File.ReadAllBytes(p);
        File.WriteAllBytes(f, bytes[..(bytes.Length / 2)]);
        await Assert.ThrowsAsync<InvalidApkException>(() => ApkInspector.InspectAsync(f));
    }

    [Fact]
    public void Corrupt_manifest_throws_invalid_apk()
    {
        Assert.Throws<InvalidApkException>(() => AxmlReader.ReadManifest([1, 2, 3]));
        Assert.Throws<InvalidApkException>(() => AxmlReader.ReadManifest([3, 0, 8, 0, 20, 0, 0, 0, 1, 0, 8, 0, 255, 255, 0, 0]));
    }

    [Fact]
    public void Signature_to_string()
    {
        Assert.Equal("nessuna", new ApkSignature(false, false).ToString());
        Assert.Equal("v1+v2/v3", new ApkSignature(true, true).ToString());
    }
}
