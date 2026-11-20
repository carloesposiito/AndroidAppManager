using System.Windows;
using System.Windows.Media;

namespace AndroidAppManager.App;

public enum DialogTone { Info, Warning, Error }

/// <summary>Dark confirmation/info dialog that replaces MessageBox. Returns true when the user confirms.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string heading, string body, string confirm, string? cancel, DialogTone tone)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = heading;
        Heading.Text = heading;
        BodyBox.Text = body;
        ConfirmBtn.Content = confirm;
        if (cancel is null) CancelBtn.Visibility = Visibility.Collapsed;
        else CancelBtn.Content = cancel;
        ToneDot.Fill = (Brush)FindResource(tone switch
        {
            DialogTone.Warning => "Amber",
            DialogTone.Error => "Rose",
            _ => "Accent"
        });
    }

    public static bool Ask(Window owner, string heading, string body, string confirm = "Continua",
        string? cancel = "Annulla", DialogTone tone = DialogTone.Info)
    {
        var dlg = new ConfirmDialog(heading, body, confirm, cancel, tone) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    /// <summary>Single-button notice.</summary>
    public static void Notify(Window owner, string heading, string body, DialogTone tone = DialogTone.Info) =>
        Ask(owner, heading, body, "Chiudi", null, tone);

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
