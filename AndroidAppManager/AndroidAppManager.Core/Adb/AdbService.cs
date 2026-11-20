using System.Diagnostics;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace AndroidAppManager.Core.Adb;

public sealed class AdbService : IAdbService
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _explicitAdbPath;
    private readonly IAdbClient _client = new AdbClient();
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private readonly HashSet<AdbEndpoint> _ourConnections = [];
    private bool _serverReady;
    private bool _ownsServer;
    private string? _adbPath;

    public AdbService(string? explicitAdbPath = null) => _explicitAdbPath = explicitAdbPath;

    public async Task EnsureServerAsync(CancellationToken ct = default)
    {
        await _serverLock.WaitAsync(ct);
        try
        {
            if (_serverReady) return;
            var path = AdbLocator.Find(_explicitAdbPath, AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("PATH")) ?? throw new AdbNotFoundException();
            var result = await Task.Run(() => new AdbServer().StartServer(path, restartServerIfNewer: false), ct);
            _adbPath = path;
            // Only a server we launched is ours to stop; one already running may serve other tools.
            _ownsServer = result is StartServerResult.Started or StartServerResult.Starting;
            _serverReady = true;
        }
        finally { _serverLock.Release(); }
    }

    public async Task<ConnectResult> ConnectAsync(AdbEndpoint endpoint, CancellationToken ct = default)
    {
        await EnsureServerAsync(ct);
        var alreadyOnline = await FindDeviceAsync(endpoint, ct) is { State: DeviceState.Online };
        string reply;
        try
        {
            reply = await _client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(ConnectionState.Failed, ex.Message);
        }

        // adb replies "failed to connect..." / "cannot connect..." with exit code 0.
        if (reply.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("cannot", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("unable", StringComparison.OrdinalIgnoreCase))
            return new(ConnectionState.Failed, reply.Trim());

        // Remember only sessions we opened: a device that was online before is not ours to drop on exit.
        if (!alreadyOnline) lock (_ourConnections) _ourConnections.Add(endpoint);

        var deadline = DateTime.UtcNow + AuthTimeout;
        while (true)
        {
            var dev = await FindDeviceAsync(endpoint, ct);
            switch (dev?.State)
            {
                case DeviceState.Online:
                    return new(ConnectionState.Connected, reply.Trim());
                case DeviceState.Offline:
                    return new(ConnectionState.Offline, "Dispositivo offline. Riavvia il debug di rete sulla TV.");
                case DeviceState.Unauthorized when DateTime.UtcNow >= deadline:
                    return new(ConnectionState.Unauthorized,
                        "Non autorizzato. Accetta il popup di debug USB sulla TV e riprova.");
                case null when DateTime.UtcNow >= deadline:
                    return new(ConnectionState.Failed, "Dispositivo non visibile dopo la connessione.");
            }
            await Task.Delay(500, ct);
        }
    }

    public async Task<string> PairAsync(AdbEndpoint pairingEndpoint, string pairingCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pairingCode) || !pairingCode.Trim().All(char.IsDigit))
            throw new ArgumentException("Il codice di pairing deve essere numerico.", nameof(pairingCode));
        await EnsureServerAsync(ct);
        var reply = await _client.PairAsync(pairingEndpoint.Host, pairingEndpoint.Port, pairingCode.Trim(), ct);
        return reply.Trim();
    }

    public async Task DisconnectAsync(AdbEndpoint endpoint, CancellationToken ct = default)
    {
        await EnsureServerAsync(ct);
        await _client.DisconnectAsync(endpoint.Host, endpoint.Port, ct);
        lock (_ourConnections) _ourConnections.Remove(endpoint);
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (!_serverReady) return;
        AdbEndpoint[] mine;
        lock (_ourConnections) mine = [.. _ourConnections];

        // A server we do not own keeps running, so close our own sessions on it explicitly.
        if (!_ownsServer)
            foreach (var ep in mine)
            {
                try { await _client.DisconnectAsync(ep.Host, ep.Port, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
            }

        if (_ownsServer && _adbPath is not null)
            await KillServerAsync(_adbPath, ct);

        lock (_ourConnections) _ourConnections.Clear();
        _serverReady = false;
        _ownsServer = false;
    }

    private static async Task KillServerAsync(string adbPath, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(adbPath, "kill-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // adb.exe vanished or cannot start: nothing more to clean up.
        }
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(AdbEndpoint endpoint, CancellationToken ct = default)
    {
        // One shell round trip instead of five.
        var output = await ShellAsync(endpoint,
            "getprop ro.product.manufacturer; getprop ro.product.model; getprop ro.build.version.release; " +
            "getprop ro.build.version.sdk; getprop ro.product.cpu.abi", ct);
        return ParseProps(endpoint.Serial, output);
    }

    public async Task<string> ShellAsync(AdbEndpoint endpoint, string command, CancellationToken ct = default)
    {
        var dev = await FindDeviceAsync(endpoint, ct)
            ?? throw new InvalidOperationException("Dispositivo non connesso.");
        var receiver = new ConsoleOutputReceiver();
        await _client.ExecuteRemoteCommandAsync(command, dev, receiver, ct);
        return receiver.ToString();
    }

    public async Task<string> InstallApkAsync(AdbEndpoint endpoint, string apkPath, bool allowDowngrade,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dev = await FindDeviceAsync(endpoint, ct)
            ?? throw new InvalidOperationException("Dispositivo non connesso.");
        // Push + `pm install` instead of the library's InstallAsync: the installer's own
        // "Success" / "Failure [CODE: ...]" text stays visible and maps to a clear message.
        var remote = $"/data/local/tmp/tvl-{Guid.NewGuid():N}.apk";
        try
        {
            await using (var stream = File.OpenRead(apkPath))
            {
                using var sync = new SyncService(_client, dev);
                Action<SyncProgressChangedEventArgs>? report = progress is null ? null
                    : e => progress.Report(e.ProgressPercentage / 100.0);
                await sync.PushAsync(stream, remote, UnixFileStatus.DefaultFileMode, DateTimeOffset.Now,
                    report, false, ct);
            }
            var flags = allowDowngrade ? "-r -d" : "-r";
            return await ShellAsync(endpoint, $"pm install {flags} {remote}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Failure [{ex.GetType().Name}: {ex.Message}{(ex.InnerException is null ? "" : " / " + ex.InnerException.Message)}]";
        }
        finally
        {
            try { await ShellAsync(endpoint, $"rm -f {remote}", CancellationToken.None); } catch { /* best effort cleanup */ }
        }
    }

    internal static DeviceInfo ParseProps(string serial, string output)
    {
        var l = output.Split('\n', StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToArray();
        string At(int i) => i < l.Length ? l[i] : "?";
        _ = int.TryParse(At(3), out var sdk);
        return new(serial, At(0), At(1), At(2), sdk, At(4));
    }

    private async Task<DeviceData?> FindDeviceAsync(AdbEndpoint endpoint, CancellationToken ct)
    {
        var devices = await _client.GetDevicesAsync(ct);
        return devices.FirstOrDefault(d => d.Serial == endpoint.Serial);
    }
}
