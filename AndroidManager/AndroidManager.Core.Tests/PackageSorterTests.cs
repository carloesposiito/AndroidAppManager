using AndroidManager.Core.Packages;

namespace AndroidManager.Core.Tests;

public class PackageSorterTests
{
    private static PackageInfo P(string name, PackageRisk risk = PackageRisk.Unknown, string? cat = null, bool off = false) =>
        new(name, PackageOrigin.System, risk, cat, off);

    private static string[] Names(IEnumerable<PackageInfo> l) => l.Select(p => p.Name).ToArray();

    [Fact]
    public void Name_is_case_insensitive_both_directions()
    {
        var items = new[] { P("b.x"), P("A.x"), P("c.x") };
        Assert.Equal(new[] { "A.x", "b.x", "c.x" }, Names(PackageSorter.Sort(items, PackageSortKey.Name, false)));
        Assert.Equal(new[] { "c.x", "b.x", "A.x" }, Names(PackageSorter.Sort(items, PackageSortKey.Name, true)));
    }

    [Fact]
    public void Risk_puts_actionable_first_and_protected_last()
    {
        var items = new[]
        {
            P("p", PackageRisk.Protected), P("u", PackageRisk.Unknown),
            P("s", PackageRisk.Suspect), P("k", PackageRisk.Known)
        };
        Assert.Equal(new[] { "k", "s", "u", "p" }, Names(PackageSorter.Sort(items, PackageSortKey.Risk, false)));
        Assert.Equal(new[] { "p", "u", "s", "k" }, Names(PackageSorter.Sort(items, PackageSortKey.Risk, true)));
    }

    [Fact]
    public void Ties_break_on_name_ascending_even_when_descending()
    {
        var items = new[] { P("b", PackageRisk.Known), P("a", PackageRisk.Known), P("c", PackageRisk.Protected) };
        Assert.Equal(new[] { "c", "a", "b" }, Names(PackageSorter.Sort(items, PackageSortKey.Risk, true)));
    }

    [Fact]
    public void Empty_category_is_always_last()
    {
        var items = new[] { P("n1"), P("g", cat: "Google"), P("n0"), P("a", cat: "App") };
        Assert.Equal(new[] { "a", "g", "n0", "n1" }, Names(PackageSorter.Sort(items, PackageSortKey.Category, false)));
        Assert.Equal(new[] { "g", "a", "n0", "n1" }, Names(PackageSorter.Sort(items, PackageSortKey.Category, true)));
    }

    [Fact]
    public void State_active_before_disabled()
    {
        var items = new[] { P("d", off: true), P("a") };
        Assert.Equal(new[] { "a", "d" }, Names(PackageSorter.Sort(items, PackageSortKey.State, false)));
        Assert.Equal(new[] { "d", "a" }, Names(PackageSorter.Sort(items, PackageSortKey.State, true)));
    }

    [Fact]
    public void Empty_and_single()
    {
        Assert.Empty(PackageSorter.Sort([], PackageSortKey.Risk, true));
        Assert.Single(PackageSorter.Sort([P("x")], PackageSortKey.Category, false));
    }

    [Fact]
    public void Does_not_mutate_input()
    {
        var items = new[] { P("b"), P("a") };
        PackageSorter.Sort(items, PackageSortKey.Name, false);
        Assert.Equal(new[] { "b", "a" }, Names(items));
    }
}
