using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Tests.Support;

internal sealed class FakeAdb(Func<string, string> shell) : IAdbService
{
    public List<string> Commands { get; } = [];

    public Task<string> ShellAsync(AdbEndpoint e, string c, CancellationToken ct = default)
    {
        Commands.Add(c);
        return Task.FromResult(shell(c));
    }
    public string InstallOutput { get; set; } = "Success";
    public List<(string Path, bool Downgrade)> Installs { get; } = [];

    public Task<string> InstallApkAsync(AdbEndpoint e, string apkPath, bool allowDowngrade,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Installs.Add((apkPath, allowDowngrade));
        return Task.FromResult(InstallOutput);
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureServerAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConnectResult> ConnectAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> PairAsync(AdbEndpoint e, string c, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DisconnectAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<DeviceInfo> GetDeviceInfoAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task PullAsync(AdbEndpoint e, string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task PushAsync(AdbEndpoint e, string localPath, string remotePath, DateTimeOffset modified, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();

    public static AdbEndpoint Ep()
    {
        AdbEndpoint.TryCreate("1.2.3.4", 5555, out var ep, out _);
        return ep!;
    }
}
