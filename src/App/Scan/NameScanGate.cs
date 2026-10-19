namespace RatScanner.Scan;

/// <summary>
/// Decides whether a name scan should run for a click. Name scans fire on every left
/// click, including every shot in raid, where the system cursor is hidden and there is
/// no inspect window to read; capturing the game frame then only costs frame time.
/// </summary>
internal static class NameScanGate
{
    /// <param name="skipWhenCursorHidden">The <c>NameScan.SkipWhenCursorHidden</c> setting.</param>
    /// <param name="cursorShowing">System cursor visibility; null when Windows cannot say.</param>
    /// <returns>True to skip. An unknown cursor state never blocks a scan.</returns>
    internal static bool ShouldSkip(bool skipWhenCursorHidden, bool? cursorShowing) =>
        skipWhenCursorHidden && cursorShowing == false;
}
