using System.IO;
using System.Windows;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Devices;
using AndroidManager.Core.Settings;

namespace AndroidManager.App;

/// <summary>What every tab needs from the shell: services, the shared device state, the log and the operation bar.</summary>
public sealed class Workspace(IAdbService adb, DeviceHub hub, Window owner,
    Action<string> log, Action<string, double?> showOp)
{
    public IAdbService Adb { get; } = adb;
    public DeviceHub Hub { get; } = hub;
    public Window Owner { get; } = owner;
    public AppSettings Settings { get; set; } = AppSettings.Load();

    public void Log(string line) => log(line);

    /// <summary>fraction null = unknown duration.</summary>
    public void ShowOp(string label, double? fraction) => showOp(label, fraction);

    /// <summary>The selected device, or null after asking the user to pick one in the left list.</summary>
    public DeviceSession? Require()
    {
        if (Hub.Selected is { } s) return s;
        ConfirmDialog.Notify(Owner, "Seleziona un dispositivo",
            "Scegli un dispositivo nella lista a sinistra, poi riprova.");
        return null;
    }

    public async Task LoadInfoAsync(DeviceSession session)
    {
        try
        {
            session.Info = await Adb.GetDeviceInfoAsync(session.Endpoint);
            Hub.NotifyChanged();
        }
        catch (Exception ex) { Log($"Info di {session.Model} non lette: {ex.Message}"); }
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Impostazioni non salvate: {ex.Message}");
        }
    }
}
