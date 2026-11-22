using System.Windows;
using System.Windows.Media;
using AndroidManager.Core.Adb;
using AndroidManager.Core.Devices;
using AndroidManager.Core.Files;
using AndroidManager.Core.Photos;

namespace AndroidManager.App;

public sealed class FolderRow
{
    public string Folder { get; init; } = "";
    public string Stats { get; set; } = "";
    public bool IsChecked { get; set; }
}

/// <summary>"Foto" tab: move photos from the Source phone to the Backup phone, delete from the source only what is verified.</summary>
public partial class PhotosView : System.Windows.Controls.UserControl
{
    private Workspace _ws = null!;
    private readonly List<FolderRow> _rows = [];
    private CancellationTokenSource? _cts;
    private string _routeKey = "";
    private string _destSerial = "";

    public PhotosView() => InitializeComponent();

    public void Init(Workspace ws)
    {
        _ws = ws;
        ws.Hub.Changed += Refresh;
        Refresh();
    }

    /// <summary>Called by the shell when the window is forced to close mid-transfer.</summary>
    public void CancelRunning() => _cts?.Cancel();

    /// <summary>The chosen backup phone: a connected device other than the selected one.</summary>
    private DeviceSession? Dest => _ws.Hub.Others.FirstOrDefault(d => d.Endpoint.Serial == _destSerial);

    private static AdbDevice AsAdb(DeviceSession d) => new(d.Endpoint.Serial, d.Model, d.Product, true, "Online");

    private void Refresh()
    {
        var hub = _ws.Hub;
        var others = hub.Others.ToList();
        SourceName.Text = hub.Selected?.Model ?? "nessuno";

        // A different pair of phones makes the folder list stale and the destination choices change.
        var key = $"{hub.Selected?.Endpoint.Serial}>{string.Join(',', others.Select(d => d.Endpoint.Serial))}";
        if (key != _routeKey)
        {
            _routeKey = key;
            if (Dest is null)
                _destSerial = others.FirstOrDefault(d => _ws.Settings.IsBackup(AsAdb(d)))?.Endpoint.Serial ?? "";
            _rows.Clear();
            FolderList.ItemsSource = null;
            PlanText.Text = "";
            FolderHint.Text = hub.Selected is null ? "" : "Premi Leggi cartelle per vedere cosa c'è sul sorgente.";
            BuildDestinations(others);
        }

        RouteHint.Text = hub.Selected is null ? "Seleziona il telefono sorgente nella lista a sinistra."
            : others.Count == 0 ? "Collega un secondo telefono: sarà la destinazione del backup."
            : Dest is null ? "Scegli il telefono di destinazione."
            : "Le foto passano dal PC: le scarico dal sorgente e le invio al backup, una alla volta.";

        var running = _cts is not null;
        var idle = !hub.Busy;
        ReadFoldersBtn.IsEnabled = idle;
        var anyChecked = _rows.Any(r => r.IsChecked);
        AnalyzeBtn.IsEnabled = idle && anyChecked;
        StartBtn.IsEnabled = idle && anyChecked;
        DeleteBox.IsEnabled = !hub.Busy;
        DestPanel.IsEnabled = !hub.Busy;
        CancelBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        AnalyzeBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildDestinations(IReadOnlyList<DeviceSession> others)
    {
        DestPanel.Children.Clear();
        DestHint.Text = others.Count == 0 ? "Nessun altro dispositivo connesso." : "";
        foreach (var d in others)
        {
            var rb = new System.Windows.Controls.RadioButton
            {
                Style = (Style)FindResource("Seg"),
                Content = d.Model,
                GroupName = "Dest",
                Tag = d.Endpoint.Serial,
                IsChecked = d.Endpoint.Serial == _destSerial
            };
            rb.Checked += OnDestChecked;
            DestPanel.Children.Add(rb);
        }
    }

    /// <summary>The destination the user picks is remembered as the backup phone.</summary>
    private void OnDestChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string serial }) return;
        _destSerial = serial;
        PlanText.Text = "";
        if (Dest is { } d)
            _ws.SaveSettings(_ws.Settings with { BackupSerial = d.Endpoint.Serial, BackupModel = d.Model, BackupProduct = d.Product });
        Refresh();
    }

    private void OnFolderToggled(object sender, RoutedEventArgs e)
    {
        PlanText.Text = "";
        Refresh();
    }

    private IReadOnlyList<string> CheckedFolders() => [.. _rows.Where(r => r.IsChecked).Select(r => r.Folder)];

    // ---------- step 2: read folders ----------

    private async void OnReadFolders(object sender, RoutedEventArgs e)
    {
        if (_ws.Require() is not { } src) return;
        _ws.Hub.SetBusy(true);
        try
        {
            _ws.ShowOp("Leggo le cartelle del sorgente", null);
            var lister = new RemoteFileLister(_ws.Adb);
            var folders = await lister.ListFoldersAsync(src.Endpoint);
            var saved = _ws.Settings.PhotoFolders.ToHashSet(StringComparer.Ordinal);
            _rows.Clear();
            for (var i = 0; i < folders.Count; i++)
            {
                _ws.ShowOp($"Leggo {folders[i]}  {i + 1}/{folders.Count}", (double)i / folders.Count);
                var s = (await lister.SummarizeAsync(src.Endpoint, [folders[i]]))[0];
                if (s.FileCount == 0) continue; // nothing to move: keep the list short
                _rows.Add(new FolderRow
                {
                    Folder = s.Folder,
                    Stats = $"{s.FileCount} file · {Fmt.Bytes(s.TotalBytes)}",
                    IsChecked = saved.Contains(s.Folder)
                });
            }
            FolderList.ItemsSource = null;
            FolderList.ItemsSource = _rows;
            FolderHint.Text = _rows.Count == 0
                ? "Nessuna cartella con file sul sorgente."
                : "Una cartella include le sue sottocartelle. Scegliendo DCIM e DCIM/Camera insieme, i file si copiano una volta sola.";
            _ws.Log($"Sorgente: {_rows.Count} cartelle con file.");
        }
        catch (Exception ex) { _ws.Log($"Lettura cartelle fallita: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    // ---------- step 3: analyze / run ----------

    private async Task<TransferPlan?> BuildPlanAsync()
    {
        var src = _ws.Hub.Selected;
        var dst = Dest;
        if (src is null || dst is null) return null;
        _ws.ShowOp("Confronto sorgente e backup", null);
        var plan = await new PhotoTransferPlanner(_ws.Adb).BuildAsync(src.Endpoint, dst.Endpoint, CheckedFolders());
        PlanText.Text = Describe(plan);
        _ws.Log(Describe(plan));
        return plan;
    }

    private static string Describe(TransferPlan plan)
    {
        var parts = new List<string> { $"Nuove: {plan.New.Count()} ({Fmt.Bytes(plan.NewBytes)})", $"già presenti: {plan.AlreadyPresent.Count()}" };
        var conflicts = plan.Conflicts.Count();
        if (conflicts > 0) parts.Add($"in conflitto: {conflicts} (non toccate)");
        if (plan.SourceUnreadable > 0) parts.Add($"illeggibili sul sorgente: {plan.SourceUnreadable}");
        return string.Join(" · ", parts);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        _ws.Hub.SetBusy(true);
        try { await BuildPlanAsync(); }
        catch (Exception ex) { _ws.Log($"Analisi fallita: {ex.Message}"); }
        finally { _ws.Hub.SetBusy(false); }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_ws.Require() is not { } src) return;
        if (Dest is not { } dst)
        {
            ConfirmDialog.Notify(_ws.Owner, "Scegli la destinazione",
                "Seleziona in alto il telefono che riceve le foto (deve essere un altro dispositivo connesso).");
            return;
        }
        var delete = DeleteBox.IsChecked == true;

        _ws.Hub.SetBusy(true);
        try
        {
            var plan = await BuildPlanAsync();
            if (plan is null) return;
            var toDelete = delete ? plan.New.Count() + plan.AlreadyPresent.Count() : 0;
            if (!plan.New.Any() && toDelete == 0)
            {
                ConfirmDialog.Notify(_ws.Owner, "Niente da fare",
                    plan.Items.Count == 0 && plan.SourceUnreadable == 0 ? "Nelle cartelle scelte non ci sono file."
                    : plan.Items.Count == 0 ? $"Nessun file leggibile nelle cartelle scelte. {Describe(plan)}"
                        : $"Il backup ha già tutto ({plan.AlreadyPresent.Count()} file). {Describe(plan)}");
                return;
            }

            var body = $"{src.Model}  ->  {dst.Model}\n\n" +
                       $"Da copiare: {plan.New.Count()} file ({Fmt.Bytes(plan.NewBytes)})\n" +
                       $"Già sul backup: {plan.AlreadyPresent.Count()}\n" +
                       (plan.Conflicts.Any() ? $"In conflitto (stesso nome, contenuto diverso): {plan.Conflicts.Count()}. Restano dove sono.\n" : "") +
                       (delete
                           ? $"\nDopo la copia controllo ogni file sul backup (stessa dimensione in byte, non il contenuto). " +
                             $"Solo i file verificati, fino a {toDelete}, vengono cancellati dal sorgente."
                           : "\nIl sorgente non viene modificato.");
            if (!ConfirmDialog.Ask(_ws.Owner, delete ? "Spostare le foto sul backup?" : "Copiare le foto sul backup?", body,
                    delete ? "Copia e cancella" : "Copia", "Annulla", delete ? DialogTone.Warning : DialogTone.Info))
                return;

            _ws.SaveSettings(_ws.Settings with { PhotoFolders = CheckedFolders() });
            await RunAsync(src, dst, plan, delete);
        }
        catch (Exception ex) { _ws.Log($"Trasferimento fallito: {ex.Message}"); }
        finally
        {
            _cts = null;
            _ws.Hub.SetBusy(false);
            Refresh();
        }
    }

    private async Task RunAsync(DeviceSession src, DeviceSession dst, TransferPlan plan, bool delete)
    {
        _cts = new CancellationTokenSource();
        Refresh();
        RunCard.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        Step2.Visibility = StepArrow.Visibility = delete ? Visibility.Visible : Visibility.Collapsed;
        SetStep(0);
        RunBar.Value = 0;

        var progress = new Progress<TransferProgress>(p =>
        {
            SetStep(p.Phase switch { TransferPhase.Verifying => 1, TransferPhase.Deleting => 2, TransferPhase.Indexing => 3, _ => 0 });
            var total = Math.Max(p.Total, 1);
            var within = p.Phase switch
            {
                TransferPhase.Downloading => p.FileFraction * 0.5,
                TransferPhase.Uploading => 0.5 + p.FileFraction * 0.5,
                _ => 0
            };
            RunBar.Value = Math.Clamp((p.Done + within) / total, 0, 1);
            var verb = p.Phase switch
            {
                TransferPhase.Downloading => "Scarico",
                TransferPhase.Uploading => "Invio",
                TransferPhase.Verifying => "Verifico",
                TransferPhase.Indexing => "Aggiorno la galleria",
                _ => "Cancello"
            };
            RunText.Text = $"{verb} {Math.Min(p.Done + 1, p.Total)}/{p.Total}  {p.Current}";
            _ws.ShowOp($"{verb} {Math.Min(p.Done + 1, p.Total)}/{p.Total}", RunBar.Value);
        });

        _ws.Log($"Trasferimento {src.Model} -> {dst.Model}: {plan.New.Count()} da copiare, cancellazione {(delete ? "attiva" : "spenta")}.");
        var result = await new PhotoTransferService(_ws.Adb).ExecuteAsync(
            src.Endpoint, dst.Endpoint, plan, delete, progress, _cts.Token);
        ShowResult(result, delete);
    }

    private void SetStep(int active)
    {
        System.Windows.Controls.TextBlock[] steps = [Step0, Step1, Step2, Step3];
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i].Foreground = (Brush)FindResource(i < active ? "Mint" : i == active ? "Text" : "Muted");
            steps[i].FontWeight = i == active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ShowResult(TransferResult r, bool delete)
    {
        var problems = r.Files.Where(f => f.Error is not null).ToList();
        var ok = !r.Cancelled && problems.Count == 0;
        ResultPanel.Visibility = Visibility.Visible;
        ResultDot.Fill = (Brush)FindResource(ok ? "Mint" : "Amber");
        ResultTitle.Text = r.Cancelled ? "Annullato" : ok ? "Completato" : "Completato con errori";
        ResultText.Text =
            $"Copiati sul backup: {r.Copied}\n" +
            $"Verificati sul backup: {r.Verified}\n" +
            (delete ? $"Cancellati dal sorgente: {r.Deleted}\n" : "Sorgente non modificato.\n") +
            (r.Conflicts > 0 ? $"In conflitto, lasciati dov'erano: {r.Conflicts}\n" : "") +
            (problems.Count > 0 ? $"Con problemi: {problems.Count}. Quei file sono ancora sul sorgente.\n" : "") +
            (r.Cancelled ? "Operazione interrotta: nulla è stato cancellato dopo l'interruzione." : "");
        ProblemsBox.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemsBox.Text = string.Join(Environment.NewLine, problems.Take(200).Select(f => $"{f.Item.Path}: {f.Error}"));
        RunText.Text = "";
        _ws.Log($"{ResultTitle.Text}: copiati {r.Copied}, verificati {r.Verified}, cancellati {r.Deleted}, problemi {problems.Count}.");
        foreach (var p in problems.Take(30)) _ws.Log($"  ERRORE {p.Item.Path}: {p.Error}");
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _ws.Log("Annullamento richiesto: finisco il file in corso.");
    }
}
