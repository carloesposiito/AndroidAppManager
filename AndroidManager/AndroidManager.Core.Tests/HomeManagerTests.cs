using AndroidManager.Core.Packages;
using AndroidManager.Core.Tests.Support;

namespace AndroidManager.Core.Tests;

public class HomeManagerTests
{
    private const string Query =
        "3 activities found:\n" +
        "  Activity #0:\n    priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n    com.vendor.launcher/.Main\n" +
        "  Activity #1:\n    priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n    it.tvlauncher.home/.MainActivity\n" +
        "  Activity #2:\n    priority=-1000 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n    com.android.tv.settings/.system.FallbackHome\n";

    private const string Chooser =
        "priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\nandroid/com.android.internal.app.ResolverActivity\n";

    private static string Resolved(string component) =>
        $"priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n{component}\n";

    /// <summary>Fake device: remembers the chosen home like the real package manager.</summary>
    private static FakeAdb Device(string? initial = null, string setOutput = "Success", bool setSticks = true)
    {
        var current = initial;
        return new FakeAdb(c =>
        {
            if (c.StartsWith("cmd package query-activities")) return Query;
            if (c.StartsWith("cmd package resolve-activity")) return current is null ? Chooser : Resolved(current);
            if (c.StartsWith("cmd package set-home-activity "))
            {
                if (setSticks) current = c["cmd package set-home-activity ".Length..];
                return setOutput;
            }
            return "";
        });
    }

    [Fact]
    public void Parse_skips_FallbackHome_and_dedupes()
    {
        var apps = HomeManager.Parse(Query + "    com.vendor.launcher/.Main\n");
        Assert.Equal(["com.vendor.launcher", "it.tvlauncher.home"], apps.Select(a => a.Package));
        Assert.Equal("it.tvlauncher.home/.MainActivity", apps[1].Component);
    }

    [Fact]
    public void Parse_rejects_components_with_shell_metacharacters()
    {
        Assert.Empty(HomeManager.Parse("    com.evil.app/.Main;reboot\n    com.evil.app/$(id)\n"));
    }

    [Fact]
    public void Parse_empty_output_gives_empty_list() => Assert.Empty(HomeManager.Parse(""));

    [Fact]
    public async Task List_reports_current_home()
    {
        var (apps, current) = await new HomeManager(Device("it.tvlauncher.home/.MainActivity")).ListAsync(FakeAdb.Ep());
        Assert.Equal(2, apps.Count);
        Assert.Equal("it.tvlauncher.home", current?.Package);
    }

    [Fact]
    public async Task List_has_no_current_when_device_shows_chooser()
    {
        var (_, current) = await new HomeManager(Device()).ListAsync(FakeAdb.Ep());
        Assert.Null(current);
    }

    [Fact]
    public async Task Set_switches_home_and_verifies()
    {
        var adb = Device("com.vendor.launcher/.Main");
        var r = await new HomeManager(adb).SetAsync(FakeAdb.Ep(), "it.tvlauncher.home");
        Assert.True(r.Success, r.Message);
        Assert.Contains("cmd package set-home-activity it.tvlauncher.home/.MainActivity", adb.Commands);
    }

    [Fact]
    public async Task Set_refuses_app_without_home_activity()
    {
        var adb = Device();
        var r = await new HomeManager(adb).SetAsync(FakeAdb.Ep(), "com.netflix.mediaclient");
        Assert.False(r.Success);
        Assert.DoesNotContain(adb.Commands, c => c.StartsWith("cmd package set-home-activity"));
    }

    [Fact]
    public async Task Set_refuses_invalid_name_without_touching_device()
    {
        var adb = Device();
        var r = await new HomeManager(adb).SetAsync(FakeAdb.Ep(), "x; reboot");
        Assert.False(r.Success);
        Assert.Empty(adb.Commands);
    }

    [Fact]
    public async Task Set_reports_command_failure()
    {
        var r = await new HomeManager(Device(setOutput: "Error: boom")).SetAsync(FakeAdb.Ep(), "it.tvlauncher.home");
        Assert.False(r.Success);
        Assert.Contains("boom", r.Message);
    }

    [Fact]
    public async Task Set_fails_when_device_ignores_the_change()
    {
        var r = await new HomeManager(Device("com.vendor.launcher/.Main", setSticks: false))
            .SetAsync(FakeAdb.Ep(), "it.tvlauncher.home");
        Assert.False(r.Success);
    }
}
