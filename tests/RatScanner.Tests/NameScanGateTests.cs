using RatScanner.Scan;
using Xunit;

namespace RatScanner.Tests;

public sealed class NameScanGateTests
{
    [Theory]
    [InlineData(true, false, true)] // in raid: cursor hidden, skip the capture
    [InlineData(true, true, false)] // inventory/menus: cursor shown, scan
    [InlineData(true, null, false)] // unknown state never blocks a scan
    [InlineData(false, false, false)] // switch disabled in config.cfg
    public void Name_scans_skip_only_when_the_cursor_is_known_hidden_and_the_switch_is_on(
        bool skipWhenCursorHidden,
        bool? cursorShowing,
        bool expectedSkip
    )
    {
        Assert.Equal(expectedSkip, NameScanGate.ShouldSkip(skipWhenCursorHidden, cursorShowing));
    }
}
