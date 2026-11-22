using System.ComponentModel;
using System.IO;
using System.Windows;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Apk;
using AndroidManager.Core.Devices;
using AndroidManager.Core.Packages;

namespace AndroidManager.App;

/// <summary>"App" tab: installed packages of the active device (remove/disable/restore/home) and APK sideload.</summary>
public partial class AppsView : System.Windows.Controls.UserControl
{
    private readonly BackupStore _backups = new(BackupStore.DefaultDirectory);
    private Workspace _ws = null!;
    private AdbEndpoint? _connected;
    private bool _busy;
    private IReadOnlyList<PackageInfo> _packages = [];
    private PackageSortKey? _sortKey;
    private bool _sortDesc;
    private IReadOnlyList<PackageRow> _systemRows = [], _thirdPartyRows = [], _disabledRows = [];
    private HashSet<string> _homePackages = [];
    private string? _currentHome;

    public AppsView() => InitializeComponent();

    public void Init(Workspace ws)
    {
        _ws = ws;
        ws.Hub.Changed += Refresh;
        Refresh();
    }

    private void SetBusy(bool busy) => _ws.Hub.SetBusy(busy);

    /// <summary>Takes the selected device, or asks the user to select one.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_connected))]
    private bool Need()
    {
        _connected = _ws.Require()?.Endpoint;
        return _connected is not null;
    }

    /// <summary>Follows the shared hub: a different active device invalidates the scanned list.</summary>
    private void Refresh()
    {
        _busy = _ws.Hub.Busy;
        var ep = _ws.Hub.ActiveEndpoint;
        if (ep?.Serial != _connected?.Serial)
        {
            _connected = ep;
            _packages = [];
            _homePackages = [];
            _currentHome = null;
            ApplyFilter();
        }
        var ready = !_busy;
        ScanBtn.IsEnabled = ready;
        SelectKnownBtn.IsEnabled = ready;
        RemoveBtn.IsEnabled = ready;
        RestoreBtn.IsEnabled = ready;
        InstallBtn.IsEnabled = ready;
        UpdateSetHomeButton();
        UpdateActionBar();
        RefreshEmptyHint();
    }

    // ---------- sideload drop (the window forwards drag events here) ----------

    public bool CanAcceptDrop(DragEventArgs e) =>
        _connected is not null && !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);

    public void DragEnterVisual()
    {
        DropOverlay.Visibility = Visibility.Visible;
    }

    public void DragLeaveVisual() => DropOverlay.Visibility = Visibility.Collapsed;

    public async Task DropAsync(string[] files)
    {
        DragLeaveVisual();
        await InstallFilesAsync(files);
    }

    // ---------- apps ----------

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (!Need()) return;
        SetBusy(true);
        try { await RescanAsync(_connected); }
        catch (Exception ex) { _ws.Log($"Scansione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async Task RescanAsync(AdbEndpoint ep)
    {
        _ws.ShowOp("Scansione dei pacchetti", null);
        _ws.Log("Scansione in corso...");
        _packages = await new PackageScanner(_ws.Adb).ScanAsync(ep);
        _ws.Log($"Scansione: {_packages.Count} pacchetti.");
        await LoadHomesAsync(ep);
        ApplyFilter();
    }

    /// <summary>A failure here only hides the home column: it must not fail the scan.</summary>
    private async Task LoadHomesAsync(AdbEndpoint ep)
    {
        try
        {
            var (apps, current) = await new HomeManager(_ws.Adb).ListAsync(ep);
            _homePackages = apps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
            _currentHome = current?.Package;
        }
        catch (Exception ex)
        {
            _homePackages = [];
            _currentHome = null;
            _ws.Log($"Home non leggibile: {ex.Message}");
        }
    }

    private string HomeLabel(string package) =>
        package == _currentHome ? "Attiva" : _homePackages.Contains(package) ? "Disponibile" : "";

    private PackageRow ToRow(PackageInfo p) => PackageRow.From(p, HomeLabel(p.Name));

    private async void OnSetHome(object sender, RoutedEventArgs e)
    {
        if (_connected is null || PackageGrid.SelectedItems.Count != 1 ||
            PackageGrid.SelectedItem is not PackageRow { CanBeHome: true } row) return;
        if (row.Name == _currentHome) { _ws.Log($"{row.Name} è già la home."); return; }

        SetBusy(true);
        try
        {
            _ws.ShowOp($"Imposto {row.Name} come home", null);
            var r = await new HomeManager(_ws.Adb).SetAsync(_connected, row.Name);
            _ws.Log(r.Success ? $"OK  {r.Message}" : $"ERRORE  {r.Message}");
            await LoadHomesAsync(_connected);
            ApplyFilter();
        }
        catch (Exception ex) { _ws.Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void UpdateSetHomeButton() =>
        SetHomeBtn.IsEnabled = !_busy && _connected is not null && PackageGrid.SelectedItems.Count == 1 &&
                               PackageGrid.SelectedItem is PackageRow { CanBeHome: true } row && row.Name != _currentHome;

    private PackageRemover NewRemover() => new(_ws.Adb, new PackageScanner(_ws.Adb), _backups);

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

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var n = PackageGrid.SelectedItems.Count;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 selezionata" : $"{n} selezionate";
        UpdateSetHomeButton();
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
        _systemRows = view.Where(p => p.Origin == PackageOrigin.System).Select(ToRow).ToList();
        _thirdPartyRows = view.Where(p => p.Origin == PackageOrigin.ThirdParty).Select(ToRow).ToList();
        _disabledRows = view.Where(p => p.IsDisabled).Select(ToRow).ToList();
        SystemRadio.Content = $"Sistema  {_systemRows.Count}";
        ThirdPartyRadio.Content = $"Terze parti  {_thirdPartyRows.Count}";
        DisabledRadio.Content = $"Disabilitate  {_disabledRows.Count}";
        ShowCurrentRows();
    }

    private bool DisabledView => DisabledRadio?.IsChecked == true;

    private void ShowCurrentRows()
    {
        if (PackageGrid is null || SystemRadio is null) return;
        PackageGrid.ItemsSource = DisabledView ? _disabledRows : SystemRadio.IsChecked == true ? _systemRows : _thirdPartyRows;
        UpdateSortArrows();
        RefreshEmptyHint();
        UpdateActionBar();
    }

    /// <summary>The disabled list swaps the remove controls for the re-enable button.</summary>
    private void UpdateActionBar()
    {
        if (ModePanel is null) return;
        var disabled = DisabledView;
        var apps = disabled ? Visibility.Collapsed : Visibility.Visible;
        ModePanel.Visibility = apps;
        SetHomeBtn.Visibility = apps;
        RemoveBtn.Visibility = apps;
        EnableBtn.Visibility = disabled ? Visibility.Visible : Visibility.Collapsed;
        EnableBtn.IsEnabled = !_busy && _connected is not null;
        SelectKnownBtn.Visibility = apps;
    }

    private void RefreshEmptyHint()
    {
        var shown = PackageGrid.Items.Count;
        EmptyHint.Text = _connected is null ? "Seleziona un dispositivo nella lista a sinistra, poi premi Scansiona."
            : _packages.Count == 0 ? "Premi Scansiona per leggere le app installate."
            : shown == 0 ? (DisabledView ? "Nessuna app disabilitata." : "Nessun pacchetto corrisponde al filtro.")
            : "";
        EmptyHint.Visibility = EmptyHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSelectKnown(object sender, RoutedEventArgs e)
    {
        PackageGrid.SelectedItems.Clear();
        foreach (var row in PackageGrid.Items.OfType<PackageRow>().Where(r => r.Risk == PackageRisk.Known))
            PackageGrid.SelectedItems.Add(row);
        if (PackageGrid.SelectedItems.Count == 0) _ws.Log("Nessun pacchetto rimovibile in questa lista.");
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (!Need()) return;
        var names = PackageGrid.SelectedItems.OfType<PackageRow>().Select(r => r.Name).ToList();
        if (names.Count == 0) { _ws.Log("Nessun pacchetto selezionato."); return; }

        SetBusy(true);
        try
        {
            var remover = NewRemover();
            _ws.ShowOp("Controllo dei pacchetti selezionati", null);
            // Protected apps are planned too; they only run after the warnings below.
            var plan = await remover.PlanAsync(_connected, SelectedMode, names, allowProtected: true);
            var guarded = ProtectedNames(plan);
            if (guarded.Count > 0 && plan.Mode == RemovalMode.Uninstall)
            {
                var list = string.Join("\n", guarded.Take(10)) + (guarded.Count > 10 ? $"\n... e altre {guarded.Count - 10}" : "");
                if (ConfirmDialog.Ask(_ws.Owner, "App protette selezionate",
                        $"Queste app sono protette:\n\n{list}\n\nRimuoverle può rendere il dispositivo instabile o inutilizzabile. " +
                        "Disabilitarle è più sicuro: restano sul dispositivo e si riabilitano dal tab Disabilitate.",
                        "Disabilita invece", "Rimuovi comunque...", DialogTone.Warning))
                {
                    plan = await remover.PlanAsync(_connected, RemovalMode.Disable, names, allowProtected: true);
                }
                else if (!ConfirmDialog.Ask(_ws.Owner, "Rimuovere le app protette?",
                             $"Confermi la rimozione di {guarded.Count} app protette?\n\n{list}\n\nL'APK di sistema resta e si ripristina dal backup.",
                             "Rimuovi protette", "Annulla", DialogTone.Warning))
                    return;
            }
            var toRemove = plan.ToRemove.ToList();
            if (toRemove.Count == 0)
            {
                ConfirmDialog.Notify(_ws.Owner, "Niente da fare", SkippedSummary(plan));
                return;
            }

            var disable = plan.Mode == RemovalMode.Disable;
            var user = plan.Items.Where(i => i.Action == PlanAction.Remove && i.Origin == PackageOrigin.ThirdParty).ToList();
            var warnUser = !disable && user.Count > 0;
            var guardedLeft = ProtectedNames(plan);
            var body = (disable ? "Le app restano sul dispositivo ma disabilitate:\n" : "Rimozione per l'utente 0 (l'APK delle app di sistema resta):\n") +
                       "\n" + string.Join("\n", toRemove.Take(25)) +
                       (toRemove.Count > 25 ? $"\n... e altre {toRemove.Count - 25}" : "") +
                       (disable && guardedLeft.Count > 0
                           ? $"\n\nAttenzione: {guardedLeft.Count} sono app protette ({string.Join(", ", guardedLeft.Take(5))}" +
                             $"{(guardedLeft.Count > 5 ? ", ..." : "")}). Se il dispositivo si comporta male, riabilitale dal tab Disabilitate."
                           : "") +
                       (warnUser
                           ? $"\n\nAttenzione: {user.Count} sono app di terze parti ({string.Join(", ", user.Take(5).Select(i => i.Name))}" +
                             $"{(user.Count > 5 ? ", ..." : "")}). Il backup non può reinstallarle: servirà l'APK. Le app di sistema si ripristinano."
                           : "") +
                       $"\n\nSaltati:\n{SkippedSummary(plan)}\n\nSalvo un backup prima di procedere.";
            if (!ConfirmDialog.Ask(_ws.Owner, disable ? $"Disabilitare {toRemove.Count} app?" : $"Rimuovere {toRemove.Count} app?",
                    body, disable ? "Disabilita" : "Rimuovi", "Annulla",
                    warnUser || guardedLeft.Count > 0 ? DialogTone.Warning : DialogTone.Info))
                return;

            var total = toRemove.Count;
            var done = 0;
            var verb = disable ? "Disabilitazione" : "Rimozione";
            _ws.ShowOp($"{verb} 0/{total}", 0);
            var progress = new Progress<RemovalResult>(r =>
            {
                done++;
                _ws.ShowOp($"{verb} {done}/{total}  {r.Name}", (double)done / total);
                _ws.Log($"{(r.Success ? "OK     " : "ERRORE ")} {r.Name} {(r.Success ? "" : r.Output)}");
            });
            var run = await remover.ExecuteAsync(_connected, plan, progress, allowProtected: true);
            _ws.Log($"Fatto: {run.Results.Count(r => r.Success)}/{run.Results.Count}. Backup: {run.BackupPath}");
            await RescanAsync(_connected);
        }
        catch (Exception ex) { _ws.Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static List<string> ProtectedNames(RemovalPlan plan) =>
        plan.Items.Where(i => i.Action == PlanAction.Remove && i.IsProtected).Select(i => i.Name).ToList();

    private async void OnEnable(object sender, RoutedEventArgs e)
    {
        if (!Need()) return;
        var names = PackageGrid.SelectedItems.OfType<PackageRow>().Select(r => r.Name).ToList();
        if (names.Count == 0) { _ws.Log("Nessun pacchetto selezionato."); return; }
        if (!ConfirmDialog.Ask(_ws.Owner, $"Riabilitare {names.Count} app?",
                string.Join("\n", names.Take(25)) + (names.Count > 25 ? $"\n... e altre {names.Count - 25}" : ""),
                "Riabilita", "Annulla")) return;

        SetBusy(true);
        try
        {
            var total = names.Count;
            var done = 0;
            _ws.ShowOp($"Riabilitazione 0/{total}", 0);
            var progress = new Progress<RemovalResult>(r =>
            {
                done++;
                _ws.ShowOp($"Riabilitazione {done}/{total}  {r.Name}", (double)done / total);
                _ws.Log($"{(r.Success ? "OK     " : "ERRORE ")} {r.Name} {(r.Success ? "" : r.Output)}");
            });
            var results = await NewRemover().EnableAsync(_connected, names, progress);
            _ws.Log($"Riabilitate {results.Count(r => r.Success)}/{results.Count}.");
            await RescanAsync(_connected);
        }
        catch (Exception ex) { _ws.Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static string SkippedSummary(RemovalPlan plan)
    {
        string Label(PlanAction a) => a switch
        {
            PlanAction.SkipProtected => "protetti",
            PlanAction.SkipNotInstalled => "non installati",
            PlanAction.SkipInvalidName => "nome non valido",
            PlanAction.SkipLastLauncher => "ultimo launcher (il dispositivo resterebbe senza home)",
            _ => ""
        };
        var lines = plan.Items.Where(i => i.Action != PlanAction.Remove)
            .GroupBy(i => i.Action).Select(g => $"- {Label(g.Key)}: {g.Count()}").ToList();
        return lines.Count == 0 ? "- nessuno" : string.Join("\n", lines);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (!Need()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Scegli il backup da ripristinare",
            Filter = "Backup (*.json)|*.json",
            InitialDirectory = Directory.Exists(BackupStore.DefaultDirectory) ? BackupStore.DefaultDirectory : null
        };
        if (dlg.ShowDialog(_ws.Owner) != true) return;

        SetBusy(true);
        try
        {
            var backup = _backups.Load(dlg.FileName);
            if (backup.Packages.Count == 0) { _ws.Log("Il backup è vuoto."); return; }
            if (backup.DeviceSerial != _connected.Serial
                && !ConfirmDialog.Ask(_ws.Owner, "Backup di un altro dispositivo",
                    $"Il backup è di {backup.DeviceSerial}, sei connesso a {_connected.Serial}.",
                    "Ripristina comunque", "Annulla", DialogTone.Warning))
                return;

            var total = backup.Packages.Count;
            var done = 0;
            _ws.ShowOp($"Ripristino 0/{total}", 0);
            var progress = new Progress<RemovalResult>(r =>
            {
                done++;
                _ws.ShowOp($"Ripristino {done}/{total}  {r.Name}", (double)done / total);
                _ws.Log($"{(r.Success ? "OK     " : "ERRORE ")} {r.Name} {(r.Success ? "" : r.Output)}");
            });
            var results = await NewRemover().RestoreAsync(_connected, backup, progress);
            _ws.Log($"Ripristinati {results.Count(r => r.Success)}/{results.Count}.");
            await RescanAsync(_connected);
        }
        catch (Exception ex) { _ws.Log($"Ripristino fallito: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    // ---------- sideload ----------

    private async void OnInstallApk(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Scegli gli APK", Filter = "APK (*.apk)|*.apk|Tutti i file|*.*", Multiselect = true };
        if (dlg.ShowDialog(_ws.Owner) == true) await InstallFilesAsync(dlg.FileNames);
    }

    private async Task InstallFilesAsync(IEnumerable<string> fileList)
    {
        if (!Need()) return;
        var files = fileList.ToList();
        var ep = _connected;
        var installer = new ApkInstaller(_ws.Adb);
        var anyInstalled = false;
        SetBusy(true);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                var tag = files.Count > 1 ? $" ({i + 1}/{files.Count})" : "";
                _ws.Log($"APK: {name}");
                try
                {
                    _ws.ShowOp($"Controllo {name}{tag}", (double)i / files.Count);
                    var report = await installer.CheckAsync(ep, file);
                    foreach (var f in report.Findings) _ws.Log($"  [{f.Severity}] {f.Message}");

                    var allowDowngrade = false;
                    var warn = report.Findings.Any(f => f.Severity == FindingSeverity.Warning);
                    if (report.HasErrors)
                    {
                        if (!report.OnlyDowngradeBlocks)
                        {
                            ConfirmDialog.Notify(_ws.Owner, "Installazione bloccata", ReportText(report), DialogTone.Error);
                            continue;
                        }
                        if (!ConfirmDialog.Ask(_ws.Owner, "Installare una versione più vecchia?",
                                ReportText(report) + "\n\nSe Android lo rifiuta, l'unica via è disinstallare prima l'app (i dati andranno persi).",
                                "Installa con downgrade", "Annulla", DialogTone.Warning)) continue;
                        allowDowngrade = true;
                    }
                    else if (!ConfirmDialog.Ask(_ws.Owner, $"Installare {Path.GetFileName(file)}?", ReportText(report),
                                 "Installa", "Annulla", warn ? DialogTone.Warning : DialogTone.Info)) continue;

                    _ws.Log("Invio e installazione in corso...");
                    var lastBucket = 0;
                    _ws.ShowOp($"Invio {name}{tag}  0%", (double)i / files.Count);
                    var progress = new Progress<double>(p =>
                    {
                        if (p >= 1) _ws.ShowOp($"Installazione di {name}{tag} sul dispositivo", null);
                        else _ws.ShowOp($"Invio {name}{tag}  {p:P0}", (i + p) / files.Count);
                        var bucket = (int)(p * 4);
                        if (bucket > lastBucket) { lastBucket = bucket; _ws.Log($"  invio {bucket * 25}%"); }
                    });
                    var res = await installer.InstallAsync(ep, report, allowDowngrade, progress);
                    _ws.ShowOp($"Verifica di {name}{tag}", null);
                    _ws.Log(res.Success ? $"OK  {res.Message}" : $"ERRORE  {res.Message}");
                    anyInstalled |= res.Success;
                }
                catch (InvalidApkException ex) { _ws.Log($"APK non valido: {ex.Message}"); }
                catch (Exception ex) { _ws.Log($"Errore: {ex.Message}"); }
            }
            if (anyInstalled) await RescanAsync(ep);
        }
        catch (Exception ex) { _ws.Log($"Operazione fallita: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private static string ReportText(ApkCheckReport r)
    {
        var m = r.Apk.Manifest;
        var abis = r.Apk.Abis.Count == 0 ? "universale" : string.Join(", ", r.Apk.Abis);
        var lines = r.Findings.Select(f => $"{(f.Severity switch { FindingSeverity.Error => "[X]", FindingSeverity.Warning => "[!]", _ => "[i]" })} {f.Message}");
        return $"{m.Package}\nVersione {m.VersionName} (codice {m.VersionCode})\n" +
               $"minSdk {m.MinSdk?.ToString() ?? "?"}, targetSdk {m.TargetSdk?.ToString() ?? "?"}, CPU: {abis}\n" +
               $"Firma: {r.Apk.Signature} (la validità del certificato la verifica il dispositivo)\n" +
               $"SHA-256: {r.Apk.Sha256}\n\n" +
               string.Join("\n", lines);
    }
}
