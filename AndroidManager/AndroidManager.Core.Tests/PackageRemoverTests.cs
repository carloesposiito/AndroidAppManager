using AndroidManager.Core.Packages;
using AndroidManager.Core.Tests.Support;

namespace AndroidManager.Core.Tests;

public class PackageRemoverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bk").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private const string HomeOut =
        "1 activities found:\n  Activity #0:\n    se.blunden.xbmclauncher/.MainActivity\n";

    private FakeAdb Device(Func<string, string>? extra = null) => new(c =>
    {
        if (extra?.Invoke(c) is { } custom) return custom;
        return c switch
        {
            "pm list packages -s" => "package:android\npackage:com.android.systemui\npackage:com.apkpure.aegon\npackage:com.android.printspooler\n",
            "pm list packages -3" => "package:com.netflix.mediaclient\npackage:se.blunden.xbmclauncher\n",
            "pm list packages -d" => "",
            var h when h.StartsWith("cmd package query-activities") => HomeOut,
            var u when u.StartsWith("pm uninstall") => "Success",
            var d when d.StartsWith("pm disable-user") => "Package x new state: disabled-user",
            var i when i.StartsWith("cmd package install-existing") => "Package x installed for user: 0",
            var e when e.StartsWith("pm enable") => "Package x new state: enabled",
            _ => ""
        };
    });

    private PackageRemover Remover(FakeAdb adb) =>
        new(adb, new PackageScanner(adb), new BackupStore(_dir));

    [Fact]
    public async Task Plan_skips_protected_absent_invalid_and_last_launcher()
    {
        var plan = await Remover(Device()).PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[]
        {
            "com.apkpure.aegon", "com.android.systemui", "com.not.installed", "bad name; rm -rf /",
            "se.blunden.xbmclauncher"
        });
        PlanAction A(string n) => plan.Items.Single(i => i.Name == n).Action;
        Assert.Equal(PlanAction.Remove, A("com.apkpure.aegon"));
        Assert.Equal(PlanAction.SkipProtected, A("com.android.systemui"));
        Assert.Equal(PlanAction.SkipNotInstalled, A("com.not.installed"));
        Assert.Equal(PlanAction.SkipInvalidName, A("bad name; rm -rf /"));
        // protected regex covers xbmclauncher too
        Assert.Equal(PlanAction.SkipProtected, A("se.blunden.xbmclauncher"));
    }

    [Fact]
    public async Task Plan_blocks_removing_every_home_launcher()
    {
        var adb = Device(c => c.StartsWith("cmd package query-activities")
            ? "2 activities found:\n  Activity #0:\n    com.vendor.home/.Main\n  Activity #1:\n    com.vendor.alt/.Main\n"
            : c == "pm list packages -3" ? "package:com.vendor.home\npackage:com.vendor.alt\n" : null);
        var plan = await Remover(adb).PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall,
            new[] { "com.vendor.home", "com.vendor.alt" });
        Assert.All(plan.Items, i => Assert.Equal(PlanAction.SkipLastLauncher, i.Action));
    }

    [Fact]
    public async Task Plan_allows_removing_one_of_two_launchers()
    {
        var adb = Device(c => c.StartsWith("cmd package query-activities")
            ? "2 activities found:\n  Activity #0:\n    com.vendor.home/.Main\n  Activity #1:\n    com.vendor.alt/.Main\n"
            : c == "pm list packages -3" ? "package:com.vendor.home\npackage:com.vendor.alt\n" : null);
        var plan = await Remover(adb).PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.vendor.alt" });
        Assert.Equal(PlanAction.Remove, Assert.Single(plan.Items).Action);
    }

    private const string RealHome =
        "2 activities found:\n  Activity #0:\n    priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n" +
        "    com.vendor.home/.MainActivity\n  Activity #1:\n    priority=-1000 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n" +
        "    com.android.tv.settings/.system.FallbackHome\n";

    private FakeAdb RealHomeDevice() => Device(c =>
        c.StartsWith("cmd package query-activities") ? RealHome
        : c == "pm list packages -3" ? "package:com.vendor.home\n" : null);

    [Fact]
    public async Task Plan_blocks_only_real_launcher_even_when_FallbackHome_exists()
    {
        var plan = await Remover(RealHomeDevice()).PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.vendor.home" });
        Assert.Equal(PlanAction.SkipLastLauncher, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public async Task Execute_rechecks_last_launcher_on_forged_or_stale_plan()
    {
        var adb = RealHomeDevice();
        var forged = new RemovalPlan(RemovalMode.Uninstall, new[] { new PlanItem("com.vendor.home", PlanAction.Remove) });
        var run = await Remover(adb).ExecuteAsync(FakeAdb.Ep(), forged);
        Assert.Empty(run.Results);
        Assert.DoesNotContain(adb.Commands, c => c.StartsWith("pm uninstall"));
    }

    [Fact]
    public async Task Plan_records_origin_and_backup_lists_third_party()
    {
        var adb = Device();
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.apkpure.aegon", "com.netflix.mediaclient" });
        Assert.Equal(PackageOrigin.ThirdParty, plan.Items.Single(i => i.Name == "com.netflix.mediaclient").Origin);
        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan);
        Assert.Equal(new[] { "com.netflix.mediaclient" }, new BackupStore(_dir).Load(run.BackupPath).ThirdPartyPackages);
    }

    [Fact]
    public async Task Restore_explains_third_party_failure()
    {
        var adb = Device(c => c.StartsWith("cmd package install-existing") ? "Failure [INSTALL_FAILED_INVALID_URI]" : null);
        var res = await Remover(adb).RestoreAsync(FakeAdb.Ep(),
            new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Uninstall, ["com.a.b"], ["com.a.b"]));
        var r = Assert.Single(res);
        Assert.False(r.Success);
        Assert.Contains("sideload", r.Output);
    }

    [Fact]
    public async Task Execute_runs_uninstall_with_keep_data_and_writes_backup()
    {
        var adb = Device();
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.apkpure.aegon", "com.netflix.mediaclient" });
        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan);

        Assert.All(run.Results, x => Assert.True(x.Success));
        Assert.Contains("pm uninstall -k --user 0 com.apkpure.aegon", adb.Commands);
        var backup = new BackupStore(_dir).Load(run.BackupPath);
        Assert.Equal(new[] { "com.apkpure.aegon", "com.netflix.mediaclient" }, backup.Packages.Order());
        Assert.Equal(RemovalMode.Uninstall, backup.Mode);
    }

    [Fact]
    public async Task Execute_falls_back_without_keep_flag_and_reports_failure()
    {
        var adb = Device(c => c.StartsWith("pm uninstall -k") ? "Failure [DELETE_FAILED_INTERNAL_ERROR]"
            : c == "pm uninstall --user 0 com.apkpure.aegon" ? "Failure [x]" : null);
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.apkpure.aegon" });
        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan);

        Assert.False(Assert.Single(run.Results).Success);
        Assert.Contains("pm uninstall --user 0 com.apkpure.aegon", adb.Commands);
        Assert.Empty(new BackupStore(_dir).Load(run.BackupPath).Packages); // only successes are restorable
    }

    [Fact]
    public async Task Execute_disable_mode_uses_disable_user()
    {
        var adb = Device();
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Disable, new[] { "com.apkpure.aegon" });
        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan);
        Assert.True(Assert.Single(run.Results).Success);
        Assert.Contains("pm disable-user --user 0 com.apkpure.aegon", adb.Commands);
    }

    [Fact]
    public async Task Execute_never_touches_skipped_items()
    {
        var adb = Device();
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Uninstall, new[] { "com.android.systemui" });
        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan);
        Assert.Empty(run.Results);
        Assert.DoesNotContain(adb.Commands, c => c.StartsWith("pm uninstall") || c.StartsWith("pm disable"));
    }

    [Fact]
    public async Task Execute_ignores_hand_built_plan_with_protected_package()
    {
        var adb = Device();
        var forged = new RemovalPlan(RemovalMode.Uninstall,
            new[] { new PlanItem("com.android.systemui", PlanAction.Remove) });
        var run = await Remover(adb).ExecuteAsync(FakeAdb.Ep(), forged);
        Assert.DoesNotContain(adb.Commands, c => c.Contains("com.android.systemui"));
        Assert.Empty(run.Results);
    }

    [Fact]
    public async Task Plan_with_allowProtected_marks_protected_items_removable()
    {
        var plan = await Remover(Device()).PlanAsync(FakeAdb.Ep(), RemovalMode.Disable,
            new[] { "com.android.systemui", "com.apkpure.aegon" }, allowProtected: true);
        var sys = plan.Items.Single(i => i.Name == "com.android.systemui");
        Assert.Equal(PlanAction.Remove, sys.Action);
        Assert.True(sys.IsProtected);
        Assert.False(plan.Items.Single(i => i.Name == "com.apkpure.aegon").IsProtected);
    }

    [Fact]
    public async Task Execute_disables_protected_only_when_allowed()
    {
        var adb = Device();
        var r = Remover(adb);
        var plan = await r.PlanAsync(FakeAdb.Ep(), RemovalMode.Disable, new[] { "com.android.systemui" }, allowProtected: true);

        var blocked = await r.ExecuteAsync(FakeAdb.Ep(), plan);
        Assert.Empty(blocked.Results);
        Assert.DoesNotContain(adb.Commands, c => c.StartsWith("pm disable"));

        var run = await r.ExecuteAsync(FakeAdb.Ep(), plan, allowProtected: true);
        Assert.True(Assert.Single(run.Results).Success);
        Assert.Contains("pm disable-user --user 0 com.android.systemui", adb.Commands);
    }

    [Fact]
    public async Task Enable_runs_pm_enable_and_rejects_invalid_names()
    {
        var adb = Device();
        var res = await Remover(adb).EnableAsync(FakeAdb.Ep(), ["com.android.systemui", "x; reboot"]);
        Assert.True(res[0].Success);
        Assert.False(res[1].Success);
        Assert.Contains("pm enable --user 0 com.android.systemui", adb.Commands);
        Assert.DoesNotContain(adb.Commands, c => c.Contains("reboot"));
    }

    [Fact]
    public async Task Restore_reinstalls_uninstalled_and_enables_disabled()
    {
        var adb = Device();
        var r = Remover(adb);
        var store = new BackupStore(_dir);

        var p1 = store.Save(new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Uninstall, ["com.a.b"]));
        var res1 = await r.RestoreAsync(FakeAdb.Ep(), store.Load(p1));
        Assert.True(Assert.Single(res1).Success);
        Assert.Contains("cmd package install-existing --user 0 com.a.b", adb.Commands);

        var p2 = store.Save(new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Disable, ["com.c.d"]));
        await r.RestoreAsync(FakeAdb.Ep(), store.Load(p2));
        Assert.Contains("pm enable --user 0 com.c.d", adb.Commands);
    }

    [Fact]
    public async Task Restore_rejects_invalid_names_from_tampered_backup()
    {
        var adb = Device();
        var store = new BackupStore(_dir);
        var p = store.Save(new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Uninstall, ["x; reboot"]));
        var res = await Remover(adb).RestoreAsync(FakeAdb.Ep(), store.Load(p));
        Assert.False(Assert.Single(res).Success);
        Assert.DoesNotContain(adb.Commands, c => c.Contains("reboot"));
    }
}

public class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bks" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Save_creates_dir_and_roundtrips()
    {
        var store = new BackupStore(_dir);
        var path = store.Save(new RemovalBackup("1.2.3.4:5555", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero),
            RemovalMode.Disable, ["a.b", "c.d"]));
        var b = store.Load(path);
        Assert.Equal("1.2.3.4:5555", b.DeviceSerial);
        Assert.Equal(RemovalMode.Disable, b.Mode);
        Assert.Equal(new[] { "a.b", "c.d" }, b.Packages);
    }

    [Fact]
    public void Two_saves_same_second_do_not_overwrite()
    {
        var store = new BackupStore(_dir);
        var b = new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Uninstall, ["a.b"]);
        Assert.NotEqual(store.Save(b), store.Save(b));
    }

    [Fact]
    public void Serial_with_colon_makes_valid_filename()
    {
        var store = new BackupStore(_dir);
        var path = store.Save(new RemovalBackup("1.2.3.4:5555", DateTimeOffset.UtcNow, RemovalMode.Uninstall, []));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_corrupt_file_throws_clear_error()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "bad.json");
        File.WriteAllText(f, "{not json");
        Assert.Throws<InvalidDataException>(() => new BackupStore(_dir).Load(f));
    }

    [Fact]
    public void List_newest_first_and_empty_when_dir_missing()
    {
        var store = new BackupStore(_dir);
        Assert.Empty(store.List());
        var a = store.Save(new RemovalBackup("s", DateTimeOffset.UtcNow.AddMinutes(-5), RemovalMode.Uninstall, ["a.b"]));
        var b = store.Save(new RemovalBackup("s", DateTimeOffset.UtcNow, RemovalMode.Uninstall, ["c.d"]));
        Assert.Equal(new[] { b, a }, store.List());
    }
}
