using System;
using System.Threading;

namespace RatScanner.Display;

/// <summary>
/// Runs <c>release</c> once after a period without <see cref="Touch"/> calls. Used to
/// tear down resources that cost something while held (an active desktop duplication
/// makes DWM copy every frame) but are cheap enough to recreate on the next use.
/// </summary>
internal sealed class IdleRelease : IDisposable
{
    private readonly TimeSpan _idleDelay;
    private readonly Timer _timer;

    internal IdleRelease(TimeSpan idleDelay, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleDelay, TimeSpan.Zero);
        _idleDelay = idleDelay;
        _timer = new Timer(_ => release(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Marks the resource as used now, restarting the idle countdown.</summary>
    internal void Touch() => _timer.Change(_idleDelay, Timeout.InfiniteTimeSpan);

    /// <summary>Stops a pending release without running it.</summary>
    internal void Cancel() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();
}
