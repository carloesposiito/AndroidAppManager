namespace AndroidAppManager.Core.Adb;

public enum ConnectionState { Connected, Unauthorized, Offline, Failed }

public sealed record ConnectResult(ConnectionState State, string Message);

public sealed record DeviceInfo(
    string Serial,
    string Manufacturer,
    string Model,
    string AndroidVersion,
    int Sdk,
    string Abi);

public interface IAdbService
{
    /// <summary>Starts the local adb server if needed. Throws <see cref="AdbNotFoundException"/> if adb.exe is missing.</summary>
    Task EnsureServerAsync(CancellationToken ct = default);

    /// <summary>adb connect, then waits for the device to leave the "unauthorized" state.</summary>
    Task<ConnectResult> ConnectAsync(AdbEndpoint endpoint, CancellationToken ct = default);

    /// <summary>adb pair (Android 11+ wireless debugging). Pairing endpoint differs from the connect port.</summary>
    Task<string> PairAsync(AdbEndpoint pairingEndpoint, string pairingCode, CancellationToken ct = default);

    Task DisconnectAsync(AdbEndpoint endpoint, CancellationToken ct = default);

    Task<DeviceInfo> GetDeviceInfoAsync(AdbEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// Call on app exit. Stops the adb server only if this service started it; otherwise just closes
    /// the device sessions it opened. Safe to call when nothing was started.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);

    /// <summary>Runs a shell command on the device and returns stdout+stderr.</summary>
    Task<string> ShellAsync(AdbEndpoint endpoint, string command, CancellationToken ct = default);

    /// <summary>Streams the APK to the device and runs the package installer. Returns the installer output ("Success" or "Failure [...]").</summary>
    Task<string> InstallApkAsync(AdbEndpoint endpoint, string apkPath, bool allowDowngrade,
        IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class AdbNotFoundException()
    : Exception("adb.exe non trovato. Mettilo nella cartella 'adb' accanto all'app o nel PATH.");
