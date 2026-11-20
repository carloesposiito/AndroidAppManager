using System.Diagnostics;
using System.Net.Sockets;
using AndroidAppManager.Core.Adb;

namespace AndroidAppManager.Core.Tests;

/// <summary>
/// Integration: drives the real adb.exe and the machine's adb server (port 5037).
/// Skipped when adb is not installed. Never run alongside other adb users.
/// </summary>
[Collection("adb-server")]
public class AdbShutdownTests
{
    private static readonly string? Adb =
        AdbLocator.Find(null, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));

    private static void RunAdb(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Adb!, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
    }

    private static bool ServerListening()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 5037).Wait(1000) && c.Connected;
        }
        catch { return false; }
    }

    private static async Task WaitFor(bool listening)
    {
        for (var i = 0; i < 30 && ServerListening() != listening; i++) await Task.Delay(100);
    }

    [Fact]
    public async Task Stops_the_server_it_started()
    {
        if (Adb is null) return;
        RunAdb("kill-server");
        await WaitFor(false);
        Assert.False(ServerListening());

        var svc = new AdbService();
        await svc.EnsureServerAsync();
        Assert.True(ServerListening());

        await svc.ShutdownAsync();
        await WaitFor(false);
        Assert.False(ServerListening());
    }

    [Fact]
    public async Task Leaves_a_server_it_did_not_start()
    {
        if (Adb is null) return;
        RunAdb("start-server");
        await WaitFor(true);
        try
        {
            var svc = new AdbService();
            await svc.EnsureServerAsync();
            await svc.ShutdownAsync();
            await Task.Delay(500);
            Assert.True(ServerListening());
        }
        finally { RunAdb("kill-server"); }
    }

    [Fact]
    public async Task Shutdown_without_start_is_a_noop_and_repeatable()
    {
        var svc = new AdbService();
        await svc.ShutdownAsync();
        await svc.ShutdownAsync();
    }

    [Fact]
    public async Task Server_can_restart_after_shutdown()
    {
        if (Adb is null) return;
        RunAdb("kill-server");
        await WaitFor(false);
        var svc = new AdbService();
        await svc.EnsureServerAsync();
        await svc.ShutdownAsync();
        await WaitFor(false);
        await svc.EnsureServerAsync();
        Assert.True(ServerListening());
        await svc.ShutdownAsync();
        await WaitFor(false);
        Assert.False(ServerListening());
    }
}
