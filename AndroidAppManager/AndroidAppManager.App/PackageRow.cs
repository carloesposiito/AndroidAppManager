using AndroidAppManager.Core.Packages;

namespace AndroidAppManager.App;

/// <summary>Display model for the package grids.</summary>
public sealed record PackageRow(string Name, string Rischio, string Categoria, string Stato, PackageRisk Risk, string Home = "")
{
    public bool CanBeHome => Home.Length > 0;

    /// <param name="home">"" when the app has no home screen, otherwise "Attiva" or "Disponibile".</param>
    public static PackageRow From(PackageInfo p, string home = "") => new(
        p.Name,
        p.Risk switch
        {
            PackageRisk.Protected => "Protetto",
            PackageRisk.Known => "Rimovibile (noto)",
            PackageRisk.Suspect => "Sospetto",
            _ => "Da valutare"
        },
        p.Category ?? "",
        p.IsDisabled ? "Disabilitato" : "Attivo",
        p.Risk,
        home);
}
