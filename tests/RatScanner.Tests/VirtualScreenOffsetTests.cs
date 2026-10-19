#nullable enable

using System;
using System.Drawing;
using System.IO;
using Xunit;

namespace RatScanner.Tests;

public sealed class VirtualScreenOffsetTests
{
    [Fact]
    public void Virtual_screen_spans_monitors_left_of_and_above_the_primary()
    {
        Rectangle bounds = VirtualScreenOffset.VirtualScreenBounds([
            new Rectangle(0, 0, 2560, 1440),
            new Rectangle(-1920, 200, 1920, 1080),
            new Rectangle(2560, -400, 1440, 2560),
        ]);

        Assert.Equal(Rectangle.FromLTRB(-1920, -400, 4000, 2160), bounds);
    }

    [Fact]
    public void Update_replaces_the_startup_origin_and_scale_and_notifies_once()
    {
        VirtualScreenOffset offset = new(0, 0);
        int changes = 0;
        offset.Changed += () => changes++;

        offset.Update(-1920, -400, 1.5);
        offset.Update(-1920, -400, 1.5);

        Assert.Equal((-1920, -400, 1.5), (offset.XOffset, offset.YOffset, offset.DeviceScale));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Dpi_change_keeps_the_origin_and_invalid_scales_fall_back_to_one()
    {
        VirtualScreenOffset offset = new(-1920, 0, 1.25);

        offset.UpdateScale(1.75);
        Assert.Equal((-1920, 0, 1.75), (offset.XOffset, offset.YOffset, offset.DeviceScale));

        offset.UpdateScale(double.NaN);
        Assert.Equal(1, offset.DeviceScale);
        offset.UpdateScale(0);
        Assert.Equal(1, offset.DeviceScale);
    }

    [Fact]
    public void Overlay_maps_physical_pixels_with_its_own_dpi_not_the_game_display_scale()
    {
        // SettingsVM.ScreenScale is the game display's DPI (or a custom/unsaved settings
        // value); the overlay's CSS pixels follow the overlay window's DPI instead.
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "RatScanner.sln")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        string overlay = File.ReadAllText(Path.Combine(root, "src", "App", "Pages", "Overlay", "Index.razor"));

        Assert.Contains("VirtualScreenOffset.DeviceScale", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenScale", overlay, StringComparison.Ordinal);
    }
}
