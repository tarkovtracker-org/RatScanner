#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;
using TarkovItem = RatScanner.TarkovDev.Item;

namespace RatScanner.Tests;

public sealed class IconWarmUpSlotSizesTests
{
    private static IEnumerable<TarkovItem> Items(int count, int width, int height) =>
        Enumerable
            .Range(0, count)
            .Select(i => new TarkovItem
            {
                Id = $"{width}x{height}-{i}",
                Width = width,
                Height = height,
            });

    [Fact]
    public void Most_common_sizes_are_warmed_first_until_coverage_is_reached()
    {
        TarkovItem[] items =
        [
            .. Items(50, 1, 1),
            .. Items(20, 2, 1),
            .. Items(15, 2, 2),
            .. Items(10, 1, 2),
            .. Items(5, 5, 2),
        ];

        IReadOnlyList<RatEye.Vector2> sizes = RatScannerMain.IconWarmUpSlotSizes(items, coverage: 0.8);

        Assert.Equal([new RatEye.Vector2(1, 1), new RatEye.Vector2(2, 1), new RatEye.Vector2(2, 2)], sizes);
    }

    [Fact]
    public void Rare_sizes_stay_lazy_and_zero_sizes_count_as_one_slot()
    {
        TarkovItem[] items = [.. Items(90, 0, 0), .. Items(10, 3, 4)];

        IReadOnlyList<RatEye.Vector2> sizes = RatScannerMain.IconWarmUpSlotSizes(items, coverage: 0.8);

        Assert.Equal([new RatEye.Vector2(1, 1)], sizes);
    }

    [Fact]
    public void Empty_catalog_warms_nothing()
    {
        Assert.Empty(RatScannerMain.IconWarmUpSlotSizes([]));
    }
}
