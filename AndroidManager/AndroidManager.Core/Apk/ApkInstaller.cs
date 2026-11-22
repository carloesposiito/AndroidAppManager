using System.Text.RegularExpressions;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Packages;

namespace AndroidManager.Core.Apk;

public sealed record ApkInstallResult(bool Success, string Message);

public sealed partial class ApkInstaller(IAdbService adb)
{
    private const string PropsCommand =
        "getprop ro.build.version.sdk; getprop ro.product.cpu.abilist; getprop ro.product.cpu.abi";

    /// <summary>Reads the APK, queries the device and returns every finding. Installs nothing.</summary>
    public async Task<ApkCheckReport> CheckAsync(AdbEndpoint ep, string apkPath, CancellationToken ct = default)
    {
        var apk = await ApkInspector.InspectAsync(apkPath, ct);
        // The package name comes from the (untrusted) APK and goes into a shell command.
        if (!PackageRemover.IsValidName(apk.Manifest.Package))
            throw new InvalidApkException($"Nome pacchetto non valido nel manifest: {apk.Manifest.Package}");

        var props = await adb.ShellAsync(ep, PropsCommand, ct);
        var df = await adb.ShellAsync(ep, "df -k /data", ct);
        var (isInstalled, version) = await QueryInstalledAsync(ep, apk.Manifest.Package, ct);
        var facts = ParseFacts(props, df, version, isInstalled);
        return new ApkCheckReport(apk, facts, ApkValidator.Validate(apk, facts));
    }

    /// <summary>Installs after a successful check, then verifies the version on the device.</summary>
    public async Task<ApkInstallResult> InstallAsync(
        AdbEndpoint ep, ApkCheckReport report, bool allowDowngrade,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (report.HasErrors && !(allowDowngrade && report.OnlyDowngradeBlocks))
            return new(false, "Installazione bloccata dai controlli: " +
                              string.Join(" ", report.Findings.Where(f => f.Severity == FindingSeverity.Error).Select(f => f.Message)));

        var output = await adb.InstallApkAsync(ep, report.Apk.Path, allowDowngrade, progress, ct);
        if (!output.Contains("Success", StringComparison.Ordinal))
            return new(false, Explain(output));

        var package = report.Apk.Manifest.Package;
        var (installed, now) = await QueryInstalledAsync(ep, package, ct);
        if (!installed)
            return new(false, "L'installer ha risposto Success ma il pacchetto non risulta sulla TV.");
        if (now is null)
            return new(true, $"Installato: {package} (versione non verificabile su questo dispositivo).");
        return now == report.Apk.Manifest.VersionCode
            ? new(true, $"Installato e verificato: {package} versione {now}.")
            : new(false, $"L'installer ha risposto Success ma la versione sul dispositivo è {now} " +
                         $"(attesa {report.Apk.Manifest.VersionCode}).");
    }

    /// <summary>Installed? and which version. Older pm lacks --show-versioncode, so fall back step by step.</summary>
    private async Task<(bool Installed, long? Version)> QueryInstalledAsync(AdbEndpoint ep, string package, CancellationToken ct)
    {
        var (installed, version) = ParseInstalled(
            await adb.ShellAsync(ep, $"pm list packages --show-versioncode {package}", ct), package);
        if (!installed)
            installed = ParseInstalled(await adb.ShellAsync(ep, $"pm list packages {package}", ct), package).Installed;
        if (installed && version is null)
            version = ParseDumpsysVersion(await adb.ShellAsync(ep, $"dumpsys package {package}", ct));
        return (installed, version);
    }

    [GeneratedRegex(@"^package:(\S+?)(?:\s+versionCode:(\d+))?\s*$", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"\bversionCode=(\d+)")]
    private static partial Regex DumpsysVersion();

    /// <summary>`pm list packages <filter>` is a substring match: only the exact package counts.</summary>
    internal static (bool Installed, long? Version) ParseInstalled(string output, string package)
    {
        foreach (Match m in PackageLine().Matches(output))
            if (m.Groups[1].Value == package)
                return (true, m.Groups[2].Success && long.TryParse(m.Groups[2].Value, out var v) ? v : null);
        return (false, null);
    }

    internal static long? ParseDumpsysVersion(string dumpsys) =>
        DumpsysVersion().Match(dumpsys) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var v) ? v : null;

    internal static DeviceFacts ParseFacts(string propsOutput, string dfOutput, long? installedVersion, bool isInstalled)
    {
        var l = propsOutput.Split('\n', StringSplitOptions.TrimEntries).ToArray();
        string At(int i) => i < l.Length ? l[i] : "";
        _ = int.TryParse(At(0), out var sdk);
        var abis = At(1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (abis.Length == 0 && At(2).Length > 0) abis = [At(2)];
        return new DeviceFacts(sdk, abis, ParseFreeBytes(dfOutput), installedVersion, isInstalled);
    }

    /// <summary>`df -k /data`: Filesystem 1K-blocks Used Available Use% Mounted. Returns -1 when unreadable.</summary>
    internal static long ParseFreeBytes(string df)
    {
        var line = df.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase));
        var cols = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return cols is { Length: >= 4 } && long.TryParse(cols[3], out var kb) ? kb * 1024 : -1;
    }

    private static readonly (string Code, string Text)[] Reasons =
    [
        ("INSTALL_FAILED_UPDATE_INCOMPATIBLE", "Firma diversa dall'app già installata. Disinstalla prima quella esistente (i dati andranno persi)."),
        ("INSTALL_FAILED_VERSION_DOWNGRADE", "Sulla TV c'è una versione più recente."),
        ("INSTALL_FAILED_INSUFFICIENT_STORAGE", "Spazio insufficiente sulla TV."),
        ("INSTALL_FAILED_OLDER_SDK", "La versione di Android della TV è troppo vecchia per questa app."),
        ("INSTALL_FAILED_NO_MATCHING_ABIS", "Architettura CPU non compatibile (librerie native per un'altra CPU)."),
        ("INSTALL_FAILED_CPU_ABI_INCOMPATIBLE", "Architettura CPU non compatibile."),
        ("INSTALL_FAILED_DEPRECATED_SDK_VERSION", "App troppo vecchia (targetSdk obsoleto) per questa versione di Android."),
        ("INSTALL_FAILED_ALREADY_EXISTS", "App già installata."),
        ("INSTALL_FAILED_USER_RESTRICTED", "Installazione bloccata dalle restrizioni dell'utente sulla TV."),
        ("INSTALL_FAILED_VERIFICATION_FAILURE", "Verifica del pacchetto fallita."),
        ("INSTALL_FAILED_MISSING_SHARED_LIBRARY", "Manca una libreria di sistema richiesta."),
        ("INSTALL_FAILED_DUPLICATE_PERMISSION", "Permesso già definito da un'altra app."),
        ("INSTALL_FAILED_INVALID_APK", "APK non valido."),
        ("INSTALL_PARSE_FAILED_NO_CERTIFICATES", "APK non firmato."),
        ("INSTALL_PARSE_FAILED", "APK illeggibile o danneggiato."),
    ];

    internal static string Explain(string output)
    {
        var text = output.Trim();
        foreach (var (code, reason) in Reasons)
            if (text.Contains(code, StringComparison.Ordinal)) return $"{reason} ({code})";
        return string.IsNullOrEmpty(text) ? "Installazione fallita senza messaggio." : $"Installazione fallita: {text}";
    }
}
