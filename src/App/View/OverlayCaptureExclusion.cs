using System;

namespace RatScanner.View;

internal static class OverlayCaptureExclusion
{
    /// <summary>WDA_EXCLUDEFROMCAPTURE requires Windows 10 2004 (build 19041) or later.</summary>
    internal static bool IsSupported { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Applies the requested capture exclusion through <paramref name="setDisplayAffinity"/>.
    /// Returns the setting now in effect, or <see langword="null"/> when the native call
    /// failed and should be retried. Unsupported Windows versions keep the default
    /// (capturable) affinity and report the request as handled so it is not retried.
    /// </summary>
    internal static bool? Apply(bool exclude, bool isSupported, Func<uint, bool> setDisplayAffinity)
    {
        if (!isSupported)
            return exclude;

        uint affinity = exclude ? OverlayNativeMethods.WdaExcludeFromCapture : OverlayNativeMethods.WdaNone;
        return setDisplayAffinity(affinity) ? exclude : null;
    }
}
