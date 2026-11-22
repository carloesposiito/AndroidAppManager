using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Adb;

namespace AndroidManager.Core.Tests.Support;

/// <summary>Several in-memory phones behind one <see cref="IAdbService"/>; understands the shell commands the photo code sends.</summary>
internal sealed partial class FakePhones : IAdbService
{
    public sealed class Phone
    {
        public Dictionary<string, (byte[] Data, DateTimeOffset Mtime)> Files { get; } = new(StringComparer.Ordinal);
        /// <summary>Paths rm cannot remove.</summary>
        public HashSet<string> Locked { get; } = [];
        /// <summary>Folder prefixes whose listing reports a permission error.</summary>
        public HashSet<string> Unreadable { get; } = [];
        public List<string> Dirs { get; } = [];
    }

    private readonly Dictionary<string, Phone> _phones = [];
    private int _pulls, _pushes;

    public List<string> Commands { get; } = [];
    public int FailPullOn { get; set; } = -1;       // 1-based call index
    public int FailPushOn { get; set; } = -1;
    public int TruncatePushOn { get; set; } = -1;   // stored file is 1 byte shorter than sent
    public Action<int>? AfterPull { get; set; }
    public bool ThrowOnIndex { get; set; }

    public Phone Add(string serial) => _phones[serial] = new Phone();
    public static AdbEndpoint Ep(string serial) => AdbEndpoint.ForSerial(serial);

    public static void Put(Phone p, string path, string content, long unixSeconds = 1_700_000_000) =>
        p.Files[path] = (Encoding.UTF8.GetBytes(content), DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    public Task<string> ShellAsync(AdbEndpoint e, string command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Commands.Add(command);
        var p = _phones[e.Serial];

        if (command.StartsWith("find '", StringComparison.Ordinal) && command.Contains("-type f -exec stat"))
        {
            var prefix = command["find '".Length..command.IndexOf("' -type f", StringComparison.Ordinal)];
            if (p.Unreadable.Any(u => prefix.StartsWith(u, StringComparison.Ordinal)))
                return Task.FromResult("stat: '/x': Permission denied\n");
            var rows = p.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => $"{f.Value.Data.Length} {f.Value.Mtime.ToUnixTimeSeconds()} {f.Key}");
            return Task.FromResult(string.Join("\n", rows) + "\n");
        }
        if (command.StartsWith("mkdir -p '", StringComparison.Ordinal))
        {
            p.Dirs.Add(command["mkdir -p '".Length..^1]);
            return Task.FromResult("");
        }
        if (MvRx().Match(command) is { Success: true } mv)
        {
            var from = mv.Groups[1].Value;
            if (!p.Files.TryGetValue(from, out var v)) return Task.FromResult("mv: No such file");
            p.Files.Remove(from);
            p.Files[mv.Groups[2].Value] = v;
            return Task.FromResult("");
        }
        if (RmRx().Match(command) is { Success: true } rm)
        {
            var path = rm.Groups[1].Value;
            var checkAfter = command.Contains("STILL_THERE");
            if (!p.Locked.Contains(path)) p.Files.Remove(path);
            return Task.FromResult(checkAfter && p.Files.ContainsKey(path) ? "STILL_THERE\n" : "");
        }
        if (command.StartsWith("screencap -p '", StringComparison.Ordinal))
        {
            Put(p, command["screencap -p '".Length..^1], "PNG");
            return Task.FromResult("");
        }
        if (command == "reboot") return Task.FromResult("");
        if (command.StartsWith("for f in ", StringComparison.Ordinal))
        {
            if (ThrowOnIndex) throw new IOException("am not available");
            return Task.FromResult("");
        }
        throw new NotSupportedException($"FakePhones: unexpected command: {command}");
    }

    public async Task PullAsync(AdbEndpoint e, string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var n = ++_pulls;
        ct.ThrowIfCancellationRequested();
        if (n == FailPullOn) throw new IOException("pull failed");
        var p = _phones[e.Serial];
        if (!p.Files.TryGetValue(remotePath, out var f)) throw new FileNotFoundException(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, f.Data, ct);
        progress?.Report(1);
        AfterPull?.Invoke(n);
    }

    public async Task PushAsync(AdbEndpoint e, string localPath, string remotePath, DateTimeOffset modified, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var n = ++_pushes;
        ct.ThrowIfCancellationRequested();
        if (n == FailPushOn) throw new IOException("push failed");
        var data = await File.ReadAllBytesAsync(localPath, ct);
        if (n == TruncatePushOn) data = data[..^1];
        _phones[e.Serial].Files[remotePath] = (data, modified);
        progress?.Report(1);
    }

    [GeneratedRegex("^mv '(.*)' '(.*)'$")] private static partial Regex MvRx();
    [GeneratedRegex("^rm -f '(.*?)'(;.*)?$")] private static partial Regex RmRx();

    public Task EnsureServerAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConnectResult> ConnectAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> PairAsync(AdbEndpoint e, string c, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DisconnectAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<DeviceInfo> GetDeviceInfoAsync(AdbEndpoint e, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> InstallApkAsync(AdbEndpoint e, string apkPath, bool allowDowngrade, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
}
