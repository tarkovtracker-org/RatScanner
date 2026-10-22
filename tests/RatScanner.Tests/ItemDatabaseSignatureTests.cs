#nullable enable

using Xunit;
using TarkovItem = RatScanner.TarkovDev.Item;

namespace RatScanner.Tests;

public sealed class ItemDatabaseSignatureTests
{
    private static TarkovItem Item(
        string id,
        int? price = 1000,
        string name = "Item",
        string shortName = "Itm",
        int width = 1,
        int height = 1,
        string background = "default"
    ) =>
        new()
        {
            Id = id,
            Name = name,
            ShortName = shortName,
            Width = width,
            Height = height,
            BackgroundColor = background,
            Avg24HPrice = price,
        };

    [Fact]
    public void Price_only_refresh_keeps_the_signature_so_the_engine_is_not_rebuilt()
    {
        TarkovItem[] before = [Item("a", price: 1000), Item("b", price: 50)];
        TarkovItem[] after = [Item("a", price: 1250), Item("b", price: null)];

        Assert.Equal(RatScannerMain.ItemDatabaseSignature(before), RatScannerMain.ItemDatabaseSignature(after));
    }

    [Fact]
    public void Reordered_payload_keeps_the_signature()
    {
        TarkovItem[] before = [Item("a"), Item("b"), Item("c")];
        TarkovItem[] after = [Item("c"), Item("a"), Item("b")];

        Assert.Equal(RatScannerMain.ItemDatabaseSignature(before), RatScannerMain.ItemDatabaseSignature(after));
    }

    [Fact]
    public void Scan_relevant_changes_change_the_signature()
    {
        long baseline = RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b")]);

        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b", name: "Renamed")]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b", shortName: "Rn")]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b", width: 2)]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b", height: 2)]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b", background: "violet")]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("z")]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a"), Item("b"), Item("c")]));
        Assert.NotEqual(baseline, RatScannerMain.ItemDatabaseSignature([Item("a")]));
    }
}
