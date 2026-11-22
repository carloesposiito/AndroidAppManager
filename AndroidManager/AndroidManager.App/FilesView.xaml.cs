using System.IO;
using System.Windows;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Files;

namespace AndroidManager.App;

/// <summary>"File" tab: folder to PC, files to the device, screenshot, reboot. Acts on the active device.</summary>
public partial class FilesView : System.Windows.Controls.UserControl
{
    private Workspace _ws = null!;

    public FilesView() => InitializeComponent();

    public void Init(Workspace ws)
    {
        _ws = ws;
        ws.Hub.Changed += Refresh;
        Refresh();
    }

    private AdbEndpoint? Ep => _ws.Hub.ActiveEndpoint;

    /// <summary>The selected device's endpoint, or null after asking the user to select one.</summary>
    private AdbEndpoint? Need() => _ws.Require()?.Endpoint;

    private void Refresh()
    {
        var hub = _ws.Hub;
        ActiveText.Text = hub.Selected is { } a
            ? $"Dispositivo: {a.Model}"
            : "Nessun dispositivo selezionato. Sceglilo nella lista a sinistra.";
        var ready = !hub.Busy;
        CopyBtn.IsEnabled = ready;
        PushBtn.IsEnabled = ready;
    }

    private void OnQuickFolder(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string folder }) FolderBox.Text = folder;
    }

    // ---------- device -> PC ----------

    private async void OnCopyToPc(object sender, RoutedEventArgs e)
    {
        if (Need() is not { } ep) return;
        if (!RemotePath.TryFolder(FolderBox.Text, out var folder))
        {
            ConfirmDialog.Notify(_ws.Owner, "Cartella non valida", "Scrivi un percorso relativo a /sdcard, per esempio DCIM/Camera.", DialogTone.Error);
            return;
        }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Dove copio {folder}? (cartella vuota)" };
        if (dlg.ShowDialog(_ws.Owner) != true) return;

        _ws.Hub.SetBusy(true);
        try
        {
            _ws.Log($"Copia di {folder} in {dlg.FolderName}");
            var progress = new Progress<FileProgress>(p =>
                _ws.ShowOp($"Copia {Math.Min(p.Done + 1, p.Total)}/{p.Total}  {p.Current}",
                    p.Total == 0 ? null : (p.Done + p.Fraction) / p.Total));
            var r = await new DeviceTools(_ws.Adb).CopyFolderToPcAsync(ep, folder, dlg.FolderName, progress);
            Report("Copia", r);
        }
        catch (Exception ex) { _ws.Log($"Copia fallita: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    // ---------- PC -> device ----------

    public bool CanAcceptDrop(DragEventArgs e) =>
        Ep is not null && !_ws.Hub.Busy && e.Data.GetDataPresent(DataFormats.FileDrop);

    public Task DropAsync(string[] files) => PushAsync(files);

    private async void OnPushFiles(object sender, RoutedEventArgs e)
    {
        if (Need() is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Scegli i file da inviare", Multiselect = true };
        if (dlg.ShowDialog(_ws.Owner) == true) await PushAsync(dlg.FileNames);
    }

    private async Task PushAsync(IReadOnlyList<string> files)
    {
        if (Need() is not { } ep) return;
        if (!RemotePath.TryFolder(TargetBox.Text, out var folder))
        {
            ConfirmDialog.Notify(_ws.Owner, "Cartella non valida", "Scrivi un percorso relativo a /sdcard, per esempio Documents.", DialogTone.Error);
            return;
        }
        var onlyFiles = files.Where(File.Exists).ToList(); // a dropped folder is not a file
        if (onlyFiles.Count < files.Count) _ws.Log($"{files.Count - onlyFiles.Count} elementi ignorati: non sono file.");
        if (onlyFiles.Count == 0) return;

        _ws.Hub.SetBusy(true);
        try
        {
            _ws.Log($"Invio di {onlyFiles.Count} file in {folder}");
            var progress = new Progress<FileProgress>(p =>
                _ws.ShowOp($"Invio {Math.Min(p.Done + 1, p.Total)}/{p.Total}  {p.Current}",
                    p.Total == 0 ? null : (p.Done + p.Fraction) / p.Total));
            var r = await new DeviceTools(_ws.Adb).PushFilesAsync(ep, onlyFiles, folder, progress);
            Report("Invio", r);
        }
        catch (Exception ex) { _ws.Log($"Invio fallito: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    private void Report(string what, BulkResult r)
    {
        _ws.Log($"{what}: {r.Copied}/{r.Total} file ok, {r.Failed} problemi.");
        foreach (var err in r.Errors.Take(30)) _ws.Log($"  ERRORE {err}");
        if (r.Errors.Count > 30) _ws.Log($"  ... e altri {r.Errors.Count - 30}");
    }
}
