using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Files;
using AndroidManager.Core.Mirror;

namespace AndroidManager.App;

/// <summary>"Dispositivo" tab: facts and tools of the selected device (left), live screen with scrcpy (right).</summary>
public partial class DeviceView : System.Windows.Controls.UserControl
{
    private Workspace _ws = null!;
    private Process? _proc;
    private string? _startedSerial;
    private bool _starting;
    private bool _userStopped;
    private (int Width, int Height)? _screen;   // phone screen size while mirroring

    public DeviceView() => InitializeComponent();

    public void Init(Workspace ws)
    {
        _ws = ws;
        ws.Hub.Changed += Refresh;
        Refresh();
        ApplyFrame();
    }

    // ---------- facts and tools ----------

    private void Refresh()
    {
        var hub = _ws.Hub;
        var s = hub.Selected;
        EmptyText.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        InfoCard.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshBtn.IsEnabled = !hub.Busy;
        ShotBtn.IsEnabled = !hub.Busy;
        RebootBtn.IsEnabled = !hub.Busy;
        if (s is not null) ShowFacts(s);

        // A different device (or none) ends the current mirror; the new one starts on its own.
        if (_startedSerial is not null && s?.Endpoint.Serial != _startedSerial)
        {
            StopMirror();
            _userStopped = false;
        }
        if (s is null) MirrorStatus.Text = "Seleziona un dispositivo per vederne lo schermo.";
        else if (_proc is null && !_starting) MirrorStatus.Text = "Premi Avvia per vedere e controllare lo schermo.";
        MirrorBtn.IsEnabled = s is not null && !_starting;
    }

    private void ShowFacts(Core.Devices.DeviceSession s)
    {
        TitleText.Text = s.Model;
        SubText.Text = s.Endpoint.IsWireless ? $"Wi-Fi · {s.Endpoint.Serial}" : $"USB · {s.Endpoint.Serial}";

        var i = s.Info;
        (string, string)[] rows =
        [
            ("Produttore", i?.Manufacturer ?? "…"),
            ("Modello", i?.Model ?? s.Model),
            ("Prodotto", s.Product.Length > 0 ? s.Product : "?"),
            ("Android", i is null ? "…" : $"{i.AndroidVersion} (SDK {i.Sdk})"),
            ("CPU (ABI)", i?.Abi ?? "…"),
            ("Batteria", i is null ? "…" : i.BatteryPercent >= 0 ? $"{i.BatteryPercent}%" : "non disponibile"),
            ("Spazio (usato / totale)", i is null ? "…" : i.Storage.Length > 0 ? i.Storage : "non disponibile"),
            ("Connessione", s.Endpoint.IsWireless ? "Wi-Fi" : "USB")
        ];

        Rows.Children.Clear();
        Rows.RowDefinitions.Clear();
        for (var r = 0; r < rows.Length; r++)
        {
            Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[r].Item1, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 6) };
            var value = new TextBlock { Text = rows[r].Item2, Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            Rows.Children.Add(label);
            Rows.Children.Add(value);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_ws.Hub.Selected is not { } s) return;
        _ws.Hub.SetBusy(true);
        try
        {
            _ws.ShowOp($"Leggo i dati di {s.Model}", null);
            await _ws.LoadInfoAsync(s);
        }
        finally { _ws.Hub.SetBusy(false); }
    }

    private async void OnScreenshot(object sender, RoutedEventArgs e)
    {
        if (_ws.Require() is not { } dev) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Salva screenshot",
            Filter = "PNG (*.png)|*.png",
            FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dlg.ShowDialog(_ws.Owner) != true) return;

        _ws.Hub.SetBusy(true);
        try
        {
            _ws.ShowOp("Screenshot", null);
            await new DeviceTools(_ws.Adb).ScreenshotAsync(dev.Endpoint, dlg.FileName);
            _ws.Log($"Screenshot salvato: {dlg.FileName}");
        }
        catch (Exception ex) { _ws.Log($"Screenshot fallito: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    private async void OnReboot(object sender, RoutedEventArgs e)
    {
        if (_ws.Require() is not { } dev) return;
        if (!ConfirmDialog.Ask(_ws.Owner, "Riavviare il dispositivo?",
                $"{dev.Model} si riavvia e la connessione cade. Un dispositivo Wi-Fi va ricollegato dopo il riavvio.",
                "Riavvia", "Annulla", DialogTone.Warning)) return;

        _ws.Hub.SetBusy(true);
        try
        {
            StopMirror();
            await new DeviceTools(_ws.Adb).RebootAsync(dev.Endpoint);
            _ws.Log($"Riavvio inviato a {dev.Model}.");
        }
        catch (Exception ex) { _ws.Log($"Riavvio fallito: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    // ---------- scrcpy ----------

    private void OnToggleMirror(object sender, RoutedEventArgs e)
    {
        if (_proc is not null) { _userStopped = true; StopMirror(); return; }
        _userStopped = false;
        _ = MaybeStartMirrorAsync();
    }

    private void OnPickScrcpy(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Scegli scrcpy.exe", Filter = "scrcpy.exe|scrcpy.exe" };
        if (dlg.ShowDialog(_ws.Owner) != true) return;
        _ws.SaveSettings(_ws.Settings with { ScrcpyPath = dlg.FileName });
        _userStopped = false;
        _ = MaybeStartMirrorAsync();
    }

    private static string ExtractedDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidManager", "scrcpy");

    private string? FindScrcpy() =>
        ScrcpyLocator.Find(_ws.Settings.ScrcpyPath, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"), ExtractedDir);

    /// <summary>Unpacks the embedded scrcpy zip into %LocalAppData%\AndroidManager\scrcpy. Returns scrcpy.exe.</summary>
    private static string UnpackScrcpy()
    {
        using var zip = typeof(DeviceView).Assembly.GetManifestResourceStream("scrcpy.zip")
            ?? throw new InvalidOperationException("Risorsa scrcpy.zip assente dall'eseguibile.");
        return ScrcpyBundle.Extract(zip, ExtractedDir);
    }

    /// <summary>Starts the mirror for the selected device. Only the user starts it (Avvia); nothing starts on its own.</summary>
    private async Task MaybeStartMirrorAsync()
    {
        if (_proc is not null || _starting || _userStopped || !IsVisible || _ws.Hub.Selected is not { } dev) return;

        var path = FindScrcpy();
        if (path is null)
        {
            // Not installed anywhere: unpack the copy shipped inside the app.
            _starting = true;
            MirrorStatus.Text = "Preparo scrcpy (prima esecuzione)...";
            try { path = await Task.Run(UnpackScrcpy); }
            catch (Exception ex) { _ws.Log($"scrcpy incluso non estraibile: {ex.Message}"); }
            finally { _starting = false; }
        }
        PickBtn.Visibility = path is null ? Visibility.Visible : Visibility.Collapsed;
        if (path is null)
        {
            MirrorStatus.Text = "scrcpy non trovato. Mettilo nella cartella \"scrcpy\" accanto all'app, nel PATH, oppure sceglilo a mano (serve la versione 2 o più recente).";
            MirrorBtn.IsEnabled = false;
            return;
        }

        _starting = true;
        MirrorBtn.IsEnabled = false;
        MirrorStatus.Text = $"Avvio su {dev.Model}...";
        var title = ScrcpyLauncher.NewWindowTitle();
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // The panel takes the phone's proportions first, so the window opens exactly the video's shape.
            _screen = null;
            try { _screen = ScreenGeometry.Parse(await _ws.Adb.ShellAsync(dev.Endpoint, ScreenGeometry.Command)); }
            catch (Exception ex) { _ws.Log($"Dimensioni schermo non lette ({ex.Message}): uso tutto il pannello."); }
            ApplyFrame();
            MirrorFrame.UpdateLayout();

            // Open at the panel's size: scrcpy lays its video out once, at creation, and fits the whole phone screen in it.
            var dpi = VisualTreeHelper.GetDpi(Host);
            var w = (int)(Host.ActualWidth * dpi.DpiScaleX);
            var h = (int)(Host.ActualHeight * dpi.DpiScaleY);
            foreach (var a in ScrcpyLauncher.Arguments(dev.Endpoint.Serial, title, w, h)) psi.ArgumentList.Add(a);
            // Same DPI awareness as this app, so sizes and positions mean the same pixels in both windows.
            psi.Environment["SDL_WINDOWS_DPI_AWARENESS"] = "permonitorv2";
            // Same adb as the rest of the app: a different adb version would restart the server and drop our sessions.
            if (AdbLocator.Find(null, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH")) is { } adb)
                psi.Environment["ADB"] = adb;

            proc = Process.Start(psi) ?? throw new InvalidOperationException("scrcpy non è partito.");
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += (_, e) => LogScrcpy(e.Data);
            proc.ErrorDataReceived += (_, e) => LogScrcpy(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            var started = proc;
            proc.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnMirrorExited(started));

            var window = IntPtr.Zero;
            for (var i = 0; i < 100 && window == IntPtr.Zero && !proc.HasExited; i++)
            {
                window = ScrcpyHost.FindByTitle(title);
                if (window == IntPtr.Zero) await Task.Delay(200);
            }
            if (window == IntPtr.Zero || !Host.Attach(window))
            {
                if (!proc.HasExited) proc.Kill(true);
                MirrorStatus.Text = "scrcpy non ha aperto la finestra. Controlla il registro eventi.";
                _ws.Log("scrcpy: finestra non trovata.");
                return;
            }

            _proc = proc;
            _startedSerial = dev.Endpoint.Serial;
            MirrorStatus.Text = $"In esecuzione su {dev.Model}. Clic e tastiera controllano il dispositivo.";
            MirrorBtn.Content = "Ferma";
            _ws.Log($"scrcpy avviato su {dev.Model}.");
        }
        catch (Exception ex)
        {
            MirrorStatus.Text = $"Avvio fallito: {ex.Message}";
            _ws.Log($"scrcpy: {ex.Message}");
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { /* gone */ }
        }
        finally
        {
            _starting = false;
            MirrorBtn.IsEnabled = _ws.Hub.Selected is not null && PickBtn.Visibility != Visibility.Visible;
        }
    }

    private const double CardPadding = 30;      // 14 padding + 1 border, both sides
    private const double CardMinWidth = 360;    // the card never gets narrower than this
    private const double LeftMinWidth = 360, ColumnGap = 18;

    /// <summary>
    /// Sizes the black frame to the phone's proportions (as large as the window height allows, never wider than the
    /// space left of the info column) and makes the card exactly as wide as the frame. Unknown screen: card at its minimum width.
    /// </summary>
    private void ApplyFrame()
    {
        var maxWidth = Math.Max(ActualWidth - LeftMinWidth - ColumnGap - CardPadding, CardMinWidth - CardPadding);
        if (_screen is { } sc && Core.Mirror.ScreenGeometry.Contain(maxWidth, MirrorArea.ActualHeight, sc.Width, sc.Height) is var (w, h) && w > 0)
        {
            MirrorFrame.Width = Math.Floor(w);
            MirrorFrame.Height = Math.Floor(h);
            MirrorCard.Width = Math.Max(MirrorFrame.Width + CardPadding, CardMinWidth);
        }
        else
        {
            MirrorFrame.Width = double.NaN;
            MirrorFrame.Height = double.NaN;
            MirrorCard.Width = CardMinWidth;
        }
    }

    private void OnAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyFrame();
    }

    private void LogScrcpy(string? line)
    {
        // scrcpy chatters on stdout; only problems are worth the event log.
        if (line is null || !(line.StartsWith("ERROR", StringComparison.Ordinal) || line.StartsWith("WARN", StringComparison.Ordinal))) return;
        Dispatcher.BeginInvoke(() => _ws.Log($"scrcpy: {line}"));
    }

    private void OnMirrorExited(Process exited)
    {
        if (_proc != exited) return; // an old process we already replaced
        Host.Detach();
        _proc = null;
        _startedSerial = null;
        _screen = null;
        ApplyFrame();
        MirrorBtn.Content = "Avvia";
        MirrorBtn.IsEnabled = _ws.Hub.Selected is not null;
        MirrorStatus.Text = _userStopped ? "Fermato." : "Il controllo si è chiuso (dispositivo scollegato o schermo non disponibile).";
    }

    /// <summary>Ends scrcpy. Also called by the shell on exit so no scrcpy process is left running.</summary>
    public void StopMirror()
    {
        var p = _proc;
        _proc = null;
        _startedSerial = null;
        Host.Detach();
        _screen = null;
        ApplyFrame();
        try { if (p is { HasExited: false }) p.Kill(true); } catch { /* already gone */ }
        MirrorBtn.Content = "Avvia";
        if (_ws.Hub.Selected is not null) MirrorStatus.Text = _userStopped ? "Fermato." : MirrorStatus.Text;
    }
}
