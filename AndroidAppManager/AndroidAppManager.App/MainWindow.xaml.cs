using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AndroidAppManager.Core.Adb;
using AndroidAppManager.Core.Apk;
using AndroidAppManager.Core.Packages;

namespace AndroidAppManager.App;

public partial class MainWindow : Window
{
    private enum Tone { Idle, Busy, Ok, Warn, Error }

    private readonly IAdbService _adb = new AdbService();
    private readonly BackupStore _backups = new(BackupStore.DefaultDirectory);
    private AdbEndpoint? _connected;
    private bool _busy;
    private bool _shutdownDone;
    private bool _sessionEnding;
    private string _deviceLine = "";
    private IReadOnlyList<PackageInfo> _packages = [];
    private PackageSortKey? _sortKey;
    private bool _sortDesc;
    private bool _marquee;
    private IReadOnlyList<PackageRow> _systemRows = [], _thirdPartyRows = [];

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Application.Current.SessionEnding += (_, _) => _sessionEnding = true;
        SetBusy(false);
        RefreshEmptyHint();
    }

    // ---------- shutdown ----------

    /// <summary>Waits for the adb cleanup (stop our server, drop our sessions) before the window really closes.</summary>
    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone) return;

        if (_sessionEnding)
        {
            // Windows is logging off: never hold it up. Short blocking cleanup on a worker thread.
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { Task.Run(() => _adb.ShutdownAsync(quick.Token)).Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
            return;
        }

        if (_busy && !ConfirmDialog.Ask(this, "Operazione in corso",
                "Un'operazione sulla TV non è finita. Chiudere ora la interrompe a metà.",
                "Chiudi comunque", "Aspetta", DialogTone.Warning))
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        SetStatus("Chiusura...", Tone.Busy, "Fermo il servizio adb.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _adb.ShutdownAsync(cts.Token);
        }
        catch { /* never block the exit on cleanup errors */ }
        _shutdownDone = true;
        Close();
    }

    // ---------- connection ----------

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(HostBox.Text, PortBox.Text, out var ep)) return;
        SetBusy(true);
        ShowOp($"Connessione a {ep!.Serial}", null);
        SetStatus("Connessione...", Tone.Busy, ep.Serial);
        try
        {
            Log($"adb connect {ep.Serial}");
            var r = await _adb.ConnectAsync(ep);
            Log(r.Message);
            if (r.State != ConnectionState.Connected)
            {
                if (r.State == ConnectionState.Unauthorized)
                    SetStatus("In attesa della TV", Tone.Warn, "Accetta il popup di debug sulla TV, poi premi Connetti.");
                else
                    SetStatus("Connessione fallita", Tone.Error, r.Message);
                return;
            }
            _connected = ep;
            var info = await _adb.GetDeviceInfoAsync(ep);
            _deviceLine = $"{info.Manufacturer} {info.Model}\nAndroid {info.AndroidVersion} (SDK {info.Sdk}) - {info.Abi}";
            ShowConnected();
        }
        catch (Exception ex)
        {
            SetStatus("Errore", Tone.Error, ex.Message);
            Log(ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (_connected is null) return;
        SetBusy(true);
        try
        {
            await _adb.DisconnectAsync(_connected);
            Log($"Disconnesso da {_connected.Serial}");
            _connected = null;
            _packages = [];
            ApplyFilter();
            SetStatus("Non connesso", Tone.Idle, "Inserisci l'indirizzo della TV.");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (!TryReadEndpoint(HostBox.Text, PairPortBox.Text, out var ep)) return;
        SetBusy(true);
        try
        {
            ShowOp($"Abbinamento con {ep!.Serial}", null);
            Log($"adb pair {ep.Serial}");
            Log(await _adb.PairAsync(ep, PairCodeBox.Text));
            Log("Ora premi Connetti con la porta di debug wireless.");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { SetBusy(false); }
    }

    private bool TryReadEndpoint(string host, string port, out AdbEndpoint? ep)
    {
        ep = null;
        if (!int.TryParse(port.Trim(), out var p)) { Log($"Porta non valida: {port}"); return false; }
        if (AdbEndpoint.TryCreate(host, p, out ep, out var err)) return true;
        Log(err!);
        return false;
    }

    // ---------- apps ----------

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_connected is null) return;
        SetBusy(true);
        try { await RescanAsync(_connected); }
        catch (Exception ex) { Log($"Scansione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async Task RescanAsync(AdbEndpoint ep)
    {
        ShowOp("Scansione dei pacchetti", null);
        Log("Scansione in corso...");
        _packages = await new PackageScanner(_adb).ScanAsync(ep);
        Log($"Scansione: {_packages.Count} pacchetti.");
        ApplyFilter();
    }

    private PackageRemover NewRemover() => new(_adb, new PackageScanner(_adb), _backups);

    private RemovalMode SelectedMode => DisableRadio.IsChecked == true ? RemovalMode.Disable : RemovalMode.Uninstall;

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>Header click: first click ascending, second descending. Sorting happens in Core so it survives filters and rescans.</summary>
    private void OnSorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!Enum.TryParse<PackageSortKey>(e.Column.SortMemberPath, out var key)) return;
        _sortDesc = _sortKey == key && !_sortDesc;
        _sortKey = key;
        ApplyFilter();
    }

    /// <summary>The grid clears column sort arrows whenever ItemsSource changes, so redraw them from our own state.</summary>
    private void UpdateSortArrows()
    {
        foreach (var c in PackageGrid.Columns)
            c.SortDirection = _sortKey is { } k && c.SortMemberPath == k.ToString()
                ? (_sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
    }

    private void OnOriginChanged(object sender, RoutedEventArgs e) => ShowCurrentRows();

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (AppsPanel is null || InstallPanel is null) return; // fires during InitializeComponent
        var apps = AppsTab.IsChecked == true;
        AppsPanel.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        InstallPanel.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var n = PackageGrid.SelectedItems.Count;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 selezionata" : $"{n} selezionate";
    }

    private void ApplyFilter()
    {
        if (PackageGrid is null || FilterBox is null || HideProtectedBox is null) return; // during InitializeComponent
        var text = FilterBox.Text.Trim();
        var hide = HideProtectedBox.IsChecked == true;
        IReadOnlyList<PackageInfo> view = _packages
            .Where(p => !hide || p.Risk != PackageRisk.Protected)
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_sortKey is { } key) view = PackageSorter.Sort(view, key, _sortDesc);
        _systemRows = view.Where(p => p.Origin == PackageOrigin.System).Select(PackageRow.From).ToList();
        _thirdPartyRows = view.Where(p => p.Origin == PackageOrigin.ThirdParty).Select(PackageRow.From).ToList();
        SystemRadio.Content = $"Sistema  {_systemRows.Count}";
        ThirdPartyRadio.Content = $"Terze parti  {_thirdPartyRows.Count}";
        ShowCurrentRows();
    }

    private void ShowCurrentRows()
    {
        if (PackageGrid is null || SystemRadio is null) return;
        PackageGrid.ItemsSource = SystemRadio.IsChecked == true ? _systemRows : _thirdPartyRows;
        UpdateSortArrows();
        RefreshEmptyHint();
    }

    private void RefreshEmptyHint()
    {
        var shown = PackageGrid.Items.Count;
        EmptyHint.Text = _connected is null ? "Connettiti alla TV per vedere le app installate."
            : _packages.Count == 0 ? "Premi Scansiona per leggere le app installate."
            : shown == 0 ? "Nessun pacchetto corrisponde al filtro."
            : "";
        EmptyHint.Visibility = EmptyHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSelectKnown(object sender, RoutedEventArgs e)
    {
        PackageGrid.SelectedItems.Clear();
        foreach (var row in PackageGrid.Items.OfType<PackageRow>().Where(r => r.Risk == PackageRisk.Known))
            PackageGrid.SelectedItems.Add(row);
        if (PackageGrid.SelectedItems.Count == 0) Log("Nessun pacchetto rimovibile in questa lista.");
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_connected is null) return;
        var names = PackageGrid.SelectedItems.OfType<PackageRow>().Select(r => r.Name).ToList();
        if (names.Count == 0) { Log("Nessun pacchetto selezionato."); return; }

        SetBusy(true);
        try
        {
            var remover = NewRemover();
            ShowOp("Controllo dei pacchetti selezionati", null);
            var plan = await remover.PlanAsync(_connected, SelectedMode, names);
            var toRemove = plan.ToRemove.ToList();
            if (toRemove.Count == 0)
            {
                ConfirmDialog.Notify(this, "Niente da fare", SkippedSummary(plan));
                return;
            }

            var disable = plan.Mode == RemovalMode.Disable;
            var user = plan.Items.Where(i => i.Action == PlanAction.Remove && i.Origin == PackageOrigin.ThirdParty).ToList();
            var warnUser = !disable && user.Count > 0;
            var body = (disable ? "Le app restano sulla TV ma disabilitate:\n" : "Rimozione per l'utente 0 (l'APK delle app di sistema resta):\n") +
                       "\n" + string.Join("\n", toRemove.Take(25)) +
                       (toRemove.Count > 25 ? $"\n... e altre {toRemove.Count - 25}" : "") +
                       (warnUser
                           ? $"\n\nAttenzione: {user.Count} sono app di terze parti ({string.Join(", ", user.Take(5).Select(i => i.Name))}" +
                             $"{(user.Count > 5 ? ", ..." : "")}). Il backup non può reinstallarle: servirà l'APK. Le app di sistema si ripristinano."
                           : "") +
                       $"\n\nSaltati:\n{SkippedSummary(plan)}\n\nSalvo un backup prima di procedere.";
            if (!ConfirmDialog.Ask(this, disable ? $"Disabilitare {toRemove.Count} app?" : $"Rimuovere {toRemove.Count} app?",
                    body, disable ? "Disabilita" : "Rimuovi", "Annulla", warnUser ? DialogTone.Warning : DialogTone.Info))
                return;

            var total = toRemove.Count;
            var done = 0;
            var verb = disable ? "Disabilitazione" : "Rimozione";
            ShowOp($"{verb} 0/{total}", 0);
            var progress = new Progress<RemovalResult>(r =>
            {
                done++;
                ShowOp($"{verb} {done}/{total}  {r.Name}", (double)done / total);
                Log($"{(r.Success ? "OK     " : "ERRORE ")} {r.Name} {(r.Success ? "" : r.Output)}");
            });
            var run = await remover.ExecuteAsync(_connected, plan, progress);
            Log($"Fatto: {run.Results.Count(r => r.Success)}/{run.Results.Count}. Backup: {run.BackupPath}");
            await RescanAsync(_connected);
        }
        catch (Exception ex) { Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static string SkippedSummary(RemovalPlan plan)
    {
        string Label(PlanAction a) => a switch
        {
            PlanAction.SkipProtected => "protetti",
            PlanAction.SkipNotInstalled => "non installati",
            PlanAction.SkipInvalidName => "nome non valido",
            PlanAction.SkipLastLauncher => "ultimo launcher (la TV resterebbe senza home)",
            _ => ""
        };
        var lines = plan.Items.Where(i => i.Action != PlanAction.Remove)
            .GroupBy(i => i.Action).Select(g => $"- {Label(g.Key)}: {g.Count()}").ToList();
        return lines.Count == 0 ? "- nessuno" : string.Join("\n", lines);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_connected is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Scegli il backup da ripristinare",
            Filter = "Backup (*.json)|*.json",
            InitialDirectory = Directory.Exists(BackupStore.DefaultDirectory) ? BackupStore.DefaultDirectory : null
        };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        try
        {
            var backup = _backups.Load(dlg.FileName);
            if (backup.Packages.Count == 0) { Log("Il backup è vuoto."); return; }
            if (backup.DeviceSerial != _connected.Serial
                && !ConfirmDialog.Ask(this, "Backup di un altro dispositivo",
                    $"Il backup è di {backup.DeviceSerial}, sei connesso a {_connected.Serial}.",
                    "Ripristina comunque", "Annulla", DialogTone.Warning))
                return;

            var total = backup.Packages.Count;
            var done = 0;
            ShowOp($"Ripristino 0/{total}", 0);
            var progress = new Progress<RemovalResult>(r =>
            {
                done++;
                ShowOp($"Ripristino {done}/{total}  {r.Name}", (double)done / total);
                Log($"{(r.Success ? "OK     " : "ERRORE ")} {r.Name} {(r.Success ? "" : r.Output)}");
            });
            var results = await NewRemover().RestoreAsync(_connected, backup, progress);
            Log($"Ripristinati {results.Count(r => r.Success)}/{results.Count}.");
            await RescanAsync(_connected);
        }
        catch (Exception ex) { Log($"Ripristino fallito: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    // ---------- sideload ----------

    private bool CanAcceptDrop(DragEventArgs e) =>
        _connected is not null && !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!CanAcceptDrop(e)) return;
        InstallTab.IsChecked = true; // show the drop zone
        DropOutline.Stroke = (Brush)FindResource("Accent");
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOutline.Stroke = (Brush)FindResource("Line");

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDropFiles(object sender, DragEventArgs e)
    {
        DropOutline.Stroke = (Brush)FindResource("Line");
        if (!CanAcceptDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        await InstallFilesAsync(files);
    }

    private async void OnInstallApk(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Scegli gli APK", Filter = "APK (*.apk)|*.apk|Tutti i file|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) == true) await InstallFilesAsync(dlg.FileNames);
    }

    private async Task InstallFilesAsync(IEnumerable<string> fileList)
    {
        if (_connected is null) return;
        var files = fileList.ToList();
        var ep = _connected;
        var installer = new ApkInstaller(_adb);
        var anyInstalled = false;
        SetBusy(true);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                var tag = files.Count > 1 ? $" ({i + 1}/{files.Count})" : "";
                Log($"APK: {name}");
                try
                {
                    ShowOp($"Controllo {name}{tag}", (double)i / files.Count);
                    var report = await installer.CheckAsync(ep, file);
                    foreach (var f in report.Findings) Log($"  [{f.Severity}] {f.Message}");

                    var allowDowngrade = false;
                    var warn = report.Findings.Any(f => f.Severity == FindingSeverity.Warning);
                    if (report.HasErrors)
                    {
                        if (!report.OnlyDowngradeBlocks)
                        {
                            ConfirmDialog.Notify(this, "Installazione bloccata", ReportText(report), DialogTone.Error);
                            continue;
                        }
                        if (!ConfirmDialog.Ask(this, "Installare una versione più vecchia?",
                                ReportText(report) + "\n\nSe Android lo rifiuta, l'unica via è disinstallare prima l'app (i dati andranno persi).",
                                "Installa con downgrade", "Annulla", DialogTone.Warning)) continue;
                        allowDowngrade = true;
                    }
                    else if (!ConfirmDialog.Ask(this, $"Installare {Path.GetFileName(file)}?", ReportText(report),
                                 "Installa", "Annulla", warn ? DialogTone.Warning : DialogTone.Info)) continue;

                    Log("Invio e installazione in corso...");
                    var lastBucket = 0;
                    ShowOp($"Invio {name}{tag}  0%", (double)i / files.Count);
                    var progress = new Progress<double>(p =>
                    {
                        if (p >= 1) ShowOp($"Installazione di {name}{tag} sulla TV", null);
                        else ShowOp($"Invio {name}{tag}  {p:P0}", (i + p) / files.Count);
                        var bucket = (int)(p * 4);
                        if (bucket > lastBucket) { lastBucket = bucket; Log($"  invio {bucket * 25}%"); }
                    });
                    var res = await installer.InstallAsync(ep, report, allowDowngrade, progress);
                    ShowOp($"Verifica di {name}{tag}", null);
                    Log(res.Success ? $"OK  {res.Message}" : $"ERRORE  {res.Message}");
                    anyInstalled |= res.Success;
                }
                catch (InvalidApkException ex) { Log($"APK non valido: {ex.Message}"); }
                catch (Exception ex) { Log($"Errore: {ex.Message}"); }
            }
            if (anyInstalled) await RescanAsync(ep);
        }
        catch (Exception ex) { Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static string ReportText(ApkCheckReport r)
    {
        var m = r.Apk.Manifest;
        var abis = r.Apk.Abis.Count == 0 ? "universale" : string.Join(", ", r.Apk.Abis);
        var lines = r.Findings.Select(f => $"{(f.Severity switch { FindingSeverity.Error => "[X]", FindingSeverity.Warning => "[!]", _ => "[i]" })} {f.Message}");
        return $"{m.Package}\nVersione {m.VersionName} (codice {m.VersionCode})\n" +
               $"minSdk {m.MinSdk?.ToString() ?? "?"}, targetSdk {m.TargetSdk?.ToString() ?? "?"}, CPU: {abis}\n" +
               $"Firma: {r.Apk.Signature} (la validità del certificato la verifica la TV)\n" +
               $"SHA-256: {r.Apk.Sha256}\n\n" +
               string.Join("\n", lines);
    }

    // ---------- shared UI state ----------

    private void ShowConnected() => SetStatus("Connesso", Tone.Ok, _deviceLine);

    private void SetStatus(string text, Tone tone, string? details)
    {
        StatusText.Text = text;
        DeviceDetails.Text = details ?? "";
        StatusDot.Fill = (Brush)FindResource(tone switch
        {
            Tone.Ok => "Mint",
            Tone.Busy => "Accent",
            Tone.Warn => "Amber",
            Tone.Error => "Rose",
            _ => "Slate"
        });
    }

    private void Log(string line) { LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd(); }

    /// <summary>Shows the running operation next to the log. fraction null = unknown duration (sweeping bar).</summary>
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (!busy) EndOp();
        var ready = !busy && _connected is not null;
        ConnectBtn.IsEnabled = !busy;
        PairBtn.IsEnabled = !busy;
        DisconnectBtn.IsEnabled = ready;
        ScanBtn.IsEnabled = ready;
        SelectKnownBtn.IsEnabled = ready;
        RemoveBtn.IsEnabled = ready;
        RestoreBtn.IsEnabled = ready;
        InstallBtn.IsEnabled = ready;
        if (PackageGrid is not null) RefreshEmptyHint();
    }
}
