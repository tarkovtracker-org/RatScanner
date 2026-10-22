using System;
using System.Collections.Generic;
using System.Drawing;

namespace RatScanner;

/// <summary>
/// Maps physical screen coordinates (scan positions) into the overlay page: the overlay
/// window's top-left corner in physical pixels, and the device scale that WebView2 uses
/// for CSS pixels in that window.
/// </summary>
/// <remarks>
/// The overlay updates this every time it is sized for a tooltip, so monitor changes
/// after startup and the overlay's own DPI are always reflected. The scale must be the
/// overlay window's DPI, not the game display's (or a custom/unsaved settings value):
/// those differ on mixed-DPI setups and shifted tooltips away from their items.
/// </remarks>
public class VirtualScreenOffset
{
    private sealed record Snapshot(int X, int Y, double DeviceScale);

    private volatile Snapshot _current;

    public VirtualScreenOffset(int x, int y, double deviceScale = 1)
    {
        _current = new Snapshot(x, y, ValidScale(deviceScale));
    }

    public int XOffset => _current.X;
    public int YOffset => _current.Y;

    /// <summary>Overlay window DPI scale (1 = 96 DPI); CSS px = physical px / this.</summary>
    public double DeviceScale => _current.DeviceScale;

    /// <summary>Raised after the offset or scale changed.</summary>
    public event Action? Changed;

    internal void Update(int x, int y, double deviceScale)
    {
        Snapshot next = new(x, y, ValidScale(deviceScale));
        if (next == _current)
            return;
        _current = next;
        Changed?.Invoke();
    }

    internal void UpdateScale(double deviceScale)
    {
        Snapshot current = _current;
        Update(current.X, current.Y, deviceScale);
    }

    /// <summary>
    /// Bounding box of all screens. The primary screen always starts at (0, 0), so the
    /// origin never lies to its right or below it.
    /// </summary>
    internal static Rectangle VirtualScreenBounds(IEnumerable<Rectangle> screens)
    {
        int left = 0;
        int top = 0;
        int right = 0;
        int bottom = 0;
        foreach (Rectangle bound in screens)
        {
            left = Math.Min(left, bound.Left);
            top = Math.Min(top, bound.Top);
            right = Math.Max(right, bound.Right);
            bottom = Math.Max(bottom, bound.Bottom);
        }
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
}
