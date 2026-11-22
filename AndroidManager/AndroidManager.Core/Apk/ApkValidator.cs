namespace AndroidManager.Core.Apk;

public enum FindingSeverity { Info, Warning, Error }

public enum FindingCode
{
    NotSigned, MinSdkTooHigh, DeprecatedTargetSdk, AbiMismatch, NoSpace,
    Downgrade, Reinstall, Upgrade, InstalledUnknownVersion, FreshInstall, NoLeanbackLauncher, PhoneOnlyLauncher
}

public sealed record Finding(FindingSeverity Severity, FindingCode Code, string Message);

/// <summary>What the target device offers, read over ADB.</summary>
public sealed record DeviceFacts(int Sdk, IReadOnlyList<string> Abis, long FreeBytes, long? InstalledVersionCode, bool IsInstalled);

public sealed record ApkCheckReport(ApkInfo Apk, DeviceFacts Device, IReadOnlyList<Finding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == FindingSeverity.Error);

    /// <summary>The only blocker is a downgrade: installable with an explicit opt-in.</summary>
    public bool OnlyDowngradeBlocks =>
        HasErrors && Findings.Where(f => f.Severity == FindingSeverity.Error).All(f => f.Code == FindingCode.Downgrade);
}

public static class ApkValidator
{
    // Android 14+ refuses to install apps targeting an SDK below 23.
    private const int MinTargetSdkEnforcedFromSdk = 34, MinAllowedTargetSdk = 23;

    public static IReadOnlyList<Finding> Validate(ApkInfo apk, DeviceFacts dev)
    {
        var f = new List<Finding>();
        var m = apk.Manifest;

        if (!apk.Signature.IsSigned)
            f.Add(new(FindingSeverity.Error, FindingCode.NotSigned, "APK non firmato: Android lo rifiuta."));

        if (m.MinSdk is { } min && min > dev.Sdk)
            f.Add(new(FindingSeverity.Error, FindingCode.MinSdkTooHigh,
                $"Richiede Android SDK {min}, la TV ha SDK {dev.Sdk}."));

        if (dev.Sdk >= MinTargetSdkEnforcedFromSdk && m.TargetSdk is { } t && t < MinAllowedTargetSdk)
            f.Add(new(FindingSeverity.Error, FindingCode.DeprecatedTargetSdk,
                $"targetSdk {t} troppo vecchio: Android {dev.Sdk} rifiuta app sotto SDK {MinAllowedTargetSdk}."));

        if (apk.Abis.Count > 0 && !apk.Abis.Intersect(dev.Abis, StringComparer.Ordinal).Any())
            f.Add(new(FindingSeverity.Error, FindingCode.AbiMismatch,
                $"Librerie native per {string.Join(", ", apk.Abis)}; la TV supporta {string.Join(", ", dev.Abis)}."));

        var needed = apk.SizeBytes * 2;
        if (dev.FreeBytes >= 0 && dev.FreeBytes < needed)
            f.Add(new(FindingSeverity.Error, FindingCode.NoSpace,
                $"Spazio insufficiente: servono ~{Mb(needed)} MB, liberi {Mb(dev.FreeBytes)} MB."));

        if (dev.IsInstalled && dev.InstalledVersionCode is null)
            f.Add(new(FindingSeverity.Warning, FindingCode.InstalledUnknownVersion,
                "App già installata ma la versione non è leggibile: nessun controllo di downgrade possibile."));
        else if (dev.InstalledVersionCode is { } inst)
        {
            if (m.VersionCode < inst)
                f.Add(new(FindingSeverity.Error, FindingCode.Downgrade,
                    $"Versione installata più recente ({inst}) di questo APK ({m.VersionCode})."));
            else if (m.VersionCode == inst)
                f.Add(new(FindingSeverity.Warning, FindingCode.Reinstall, $"Stessa versione già installata ({inst}): reinstallazione."));
            else
                f.Add(new(FindingSeverity.Info, FindingCode.Upgrade, $"Aggiornamento da {inst} a {m.VersionCode}."));
        }
        else
            f.Add(new(FindingSeverity.Info, FindingCode.FreshInstall, "Nuova installazione."));

        if (!m.HasLeanbackLauncher && m.HasLauncherActivity)
            f.Add(new(FindingSeverity.Warning, FindingCode.PhoneOnlyLauncher,
                "App per telefono: non compare nella home della TV (serve LEANBACK_LAUNCHER). Si apre solo da un launcher che mostra tutte le app."));
        else if (!m.HasLeanbackLauncher)
            f.Add(new(FindingSeverity.Warning, FindingCode.NoLeanbackLauncher,
                "Nessuna activity avviabile: libreria o servizio, non comparirà tra le app."));

        return f;
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
