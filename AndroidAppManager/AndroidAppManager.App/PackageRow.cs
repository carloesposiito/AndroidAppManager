using AndroidAppManager.Core.Packages;

namespace AndroidAppManager.App;

/// <summary>Display model for the package grids.</summary>
public sealed record PackageRow(string Name, string Rischio, string Categoria, string Stato, PackageRisk Risk)
{
    public static PackageRow From(PackageInfo p) => new(
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
        p.Risk);
}
