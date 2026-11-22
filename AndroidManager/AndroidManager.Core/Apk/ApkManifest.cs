namespace AndroidManager.Core.Apk;

/// <summary>Fields of AndroidManifest.xml that matter before installing on a TV.</summary>
public sealed record ApkManifest(
    string Package,
    long VersionCode,
    string? VersionName,
    int? MinSdk,
    int? TargetSdk,
    bool HasLauncherActivity,
    bool HasLeanbackLauncher,
    bool RequiresLeanback);

public sealed class InvalidApkException(string message, Exception? inner = null) : Exception(message, inner);
