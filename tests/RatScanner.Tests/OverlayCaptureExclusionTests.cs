using System.Collections.Generic;
using RatScanner.View;
using Xunit;

namespace RatScanner.Tests;

public sealed class OverlayCaptureExclusionTests
{
    [Theory]
    [InlineData(true, OverlayNativeMethods.WdaExcludeFromCapture)]
    [InlineData(false, OverlayNativeMethods.WdaNone)]
    public void Successful_call_reports_the_requested_setting_as_applied(bool exclude, uint expectedAffinity)
    {
        List<uint> calls = [];

        bool? applied = OverlayCaptureExclusion.Apply(
            exclude,
            isSupported: true,
            affinity =>
            {
                calls.Add(affinity);
                return true;
            }
        );

        Assert.Equal(exclude, applied);
        Assert.Equal([expectedAffinity], calls);
    }

    [Fact]
    public void Failed_call_is_not_reported_as_applied_so_the_next_tooltip_retries()
    {
        bool? applied = OverlayCaptureExclusion.Apply(true, isSupported: true, _ => false);

        Assert.Null(applied);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unsupported_windows_skips_the_native_call_without_retrying(bool exclude)
    {
        int calls = 0;

        bool? applied = OverlayCaptureExclusion.Apply(
            exclude,
            isSupported: false,
            _ =>
            {
                calls++;
                return false;
            }
        );

        Assert.Equal(exclude, applied);
        Assert.Equal(0, calls);
    }
}
