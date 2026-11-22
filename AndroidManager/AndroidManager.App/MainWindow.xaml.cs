using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Devices;

namespace AndroidManager.App;

/// <summary>One line of the connected-devices list.</summary>
public sealed record DeviceRow(string Serial, string Title, string Sub, bool IsSelected, bool CanSelect, Brush Dot);

/// <summary>Shell: devices + connection + log (left), tabs Dispositivo / App / File / Foto (right). Tab logic lives in the views.</summary>
public partial class MainWindow : Window
{
    private enum Tab { Device, Apps, Files, Photos }

    private readonly IAdbService _adb = new AdbService();
    private readonly DeviceHub _hub = new();
    private readonly Workspace _ws;
    private IReadOnlyList<AdbDevice> _found = [];
    private bool _shutdownDone;
    private bool _sessionEnding;
    private bool _marquee;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        // Never open larger than the usable screen: the bottom of the window would fall off a small laptop display.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Application.Current.SessionEnding += (_, _) => _sessionEnding = true;

        _ws = new Workspace(_adb, _hub, this, Log, ShowOp);
        DevicePage.Init(_ws);
        AppsPage.Init(_ws);
        FilesPage.Init(_ws);
        PhotosPage.Init(_ws);
        _hub.Changed += OnHubChanged;

        HostBox.Text = _ws.Settings.LastHost;
        PortBox.Text = _ws.Settings.LastPort.ToString();
        OnHubChanged();
        Loaded += async (_, _) => await ScanAsync(); // pick up USB phones right away
    }

    // ---------- shared state -> UI ----------

    private void OnHubChanged()
    {
        var busy = _hub.Busy;
        if (!busy) EndOp();
        ScanBtn.IsEnabled = !busy;
        ConnectBtn.IsEnabled = !busy;
        PairBtn.IsEnabled = !busy;
        DisconnectBtn.IsEnabled = !busy;
        DisconnectBtn.Visibility = _hub.Selected?.Endpoint.IsWireless == true ? Visibility.Visible : Visibility.Collapsed;
        TabDevice.IsEnabled = TabApps.IsEnabled = TabFiles.IsEnabled = TabPhotos.IsEnabled = !busy;
        UpdateDeviceList();
    }

    private void UpdateDeviceList()
    {
        DeviceList.ItemsSource = _found.Select(d =>
        {
            var selected = _hub.Selected?.Endpoint.Serial == d.Serial;
            var kind = AdbEndpoint.FromSerial(d.Serial).IsWireless ? "Wi-Fi" : "USB";
            return new DeviceRow(d.Serial, d.Model,
                d.Online ? $"{kind} · {d.Serial}" : $"{kind} · {d.Serial} · {d.State}",
                selected, d.Online && !_hub.Busy,
                (Brush)FindResource(d.Online ? "Mint" : "Amber"));
        }).ToList();
        NoDevices.Visibility = _found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Tab CurrentTab => TabApps.IsChecked == true ? Tab.Apps : TabFiles.IsChecked == true ? Tab.Files
        : TabPhotos.IsChecked == true ? Tab.Photos : Tab.Device;

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (DevicePage is null || AppsPage is null || FilesPage is null || PhotosPage is null) return; // fires during InitializeComponent
        var tab = CurrentTab;
        DevicePage.Visibility = tab == Tab.Device ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = tab == Tab.Apps ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = tab == Tab.Files ? Visibility.Visible : Visibility.Collapsed;
        PhotosPage.Visibility = tab == Tab.Photos ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- shutdown ----------

    /// <summary>Waits for the adb cleanup (stop our server, drop our sessions) before the window really closes.</summary>
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone) return;

        if (_sessionEnding)
        {
            DevicePage.StopMirror();
            // Windows is logging off: never hold it up. Short blocking cleanup on a worker thread.
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { Task.Run(() => _adb.ShutdownAsync(quick.Token)).Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
            return;
        }

        if (_hub.Busy)
        {
            if (!ConfirmDialog.Ask(this, "Operazione in corso",
                    "Un'operazione sul dispositivo non è finita. Chiudere ora la interrompe a metà. " +
                    "Un trasferimento foto non cancella nulla dal sorgente se non è stato verificato.",
                    "Chiudi comunque", "Aspetta", DialogTone.Warning))
            {
                e.Cancel = true;
                return;
            }
            PhotosPage.CancelRunning();
        }
        DevicePage.StopMirror(); // no scrcpy process may outlive the window

        e.Cancel = true;
        IsEnabled = false;
        ShowOp("Chiusura: fermo il servizio adb", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _adb.ShutdownAsync(cts.Token);
        }
        catch { /* never block the exit on cleanup errors */ }
        _shutdownDone = true;
        Close();
    }

    // ---------- devices: find, pick, connect, pair, disconnect ----------

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        _hub.SetBusy(true);
        try { await ScanCoreAsync(); }
        catch (Exception ex) { Log($"Ricerca dispositivi fallita: {ex.Message}"); }
        finally { _hub.SetBusy(false); }
    }

    /// <summary>Lists adb devices, refreshes the shared list, and loads info for the ones not read yet.</summary>
    private async Task ScanCoreAsync()
    {
        ShowOp("Cerco dispositivi", null);
        _found = await _adb.ListDevicesAsync();
        _hub.SetDevices(_found.Where(d => d.Online)
            .Select(d => new DeviceSession(AdbEndpoint.FromSerial(d.Serial), d.Model, d.Product)));
        foreach (var d in _found.Where(d => !d.Online))
            Log($"{d.Model} ({d.Serial}): {d.State}. Accetta il popup di debug sul telefono.");
        Log($"Dispositivi collegati: {_hub.Devices.Count}.");
        UpdateDeviceList();
        foreach (var s in _hub.Devices.Where(s => s.Info is null)) await _ws.LoadInfoAsync(s);
    }

    private async void OnPickDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string serial } || _hub.Busy) return;
        _hub.Select(serial);
        UpdateDeviceList();
        if (_hub.Selected is { Info: null } s) await _ws.LoadInfoAsync(s);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(HostBox.Text, PortBox.Text, out var ep)) return;
        _hub.SetBusy(true);
        try
        {
            ShowOp($"Connessione a {ep!.Serial}", null);
            Log($"adb connect {ep.Serial}");
            var r = await _adb.ConnectAsync(ep);
            Log(r.Message);
            if (r.State != ConnectionState.Connected) return;
            _ws.SaveSettings(_ws.Settings with { LastHost = ep.Host, LastPort = ep.Port });
            await ScanCoreAsync();
            _hub.Select(ep.Serial); // the device the user just connected is the one they want to work on
        }
        catch (Exception ex) { Log($"Connessione fallita: {ex.Message}"); }
        finally { _hub.SetBusy(false); }
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(HostBox.Text, PairPortBox.Text, out var ep)) return;
        _hub.SetBusy(true);
        try
        {
            ShowOp($"Abbinamento con {ep!.Serial}", null);
            Log($"adb pair {ep.Serial}");
            Log(await _adb.PairAsync(ep, PairCodeBox.Text));
            Log("Ora premi Connetti con la porta di debug wireless.");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { _hub.SetBusy(false); }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (_hub.Selected is not { } s || !s.Endpoint.IsWireless) return;
        _hub.SetBusy(true);
        try
        {
            await _adb.DisconnectAsync(s.Endpoint);
            Log($"{s.Model} scollegato.");
            _found = [.. _found.Where(d => d.Serial != s.Endpoint.Serial)];
            _hub.SetDevices(_hub.Devices.Where(d => d != s));
            UpdateDeviceList();
        }
        catch (Exception ex) { Log($"Scollegamento fallito: {ex.Message}"); }
        finally { _hub.SetBusy(false); }
    }

    private bool TryReadEndpoint(string host, string port, out AdbEndpoint? ep)
    {
        ep = null;
        if (!int.TryParse(port.Trim(), out var p)) { Log($"Porta non valida: {port}"); return false; }
        if (AdbEndpoint.TryCreate(host, p, out ep, out var err)) return true;
        Log(err!);
        return false;
    }

    // ---------- drag & drop: APKs on the App tab, files on the File tab ----------

    private bool CanAcceptDrop(DragEventArgs e) => CurrentTab switch
    {
        Tab.Apps => AppsPage.CanAcceptDrop(e),
        Tab.Files => FilesPage.CanAcceptDrop(e),
        _ => false
    };

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (CurrentTab == Tab.Apps && AppsPage.CanAcceptDrop(e)) AppsPage.DragEnterVisual();
    }

    private void OnDragLeave(object sender, DragEventArgs e) => AppsPage.DragLeaveVisual();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDropFiles(object sender, DragEventArgs e)
    {
        AppsPage.DragLeaveVisual();
        if (!CanAcceptDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (CurrentTab == Tab.Apps) await AppsPage.DropAsync(files);
        else await FilesPage.DropAsync(files);
    }

    // ---------- log and operation bar ----------

    private void Log(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    /// <summary>Shows the running operation above the log. fraction null = unknown duration (sweeping bar).</summary>
    private void ShowOp(string label, double? fraction)
    {
        OpPanel.Visibility = Visibility.Visible;
        OpText.Text = label;
        if (fraction is { } f)
        {
            if (_marquee) { OpBar.BeginAnimation(RangeBase.ValueProperty, null); _marquee = false; }
            OpBar.Value = Math.Clamp(f, 0, 1);
        }
        else if (!_marquee)
        {
            _marquee = true;
            OpBar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.1))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private void EndOp()
    {
        OpBar.BeginAnimation(RangeBase.ValueProperty, null);
        _marquee = false;
        OpBar.Value = 0;
        OpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();
}
