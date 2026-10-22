using System;
using System.Threading;

namespace RatScanner.Display;

/// <summary>
/// Runs <c>release</c> once after a period without <see cref="Touch"/> calls. Used to
/// tear down resources that cost something while held (an active desktop duplication
/// makes DWM copy every frame) but are cheap enough to recreate on the next use.
/// </summary>
/// <remarks>
/// <c>release</c> runs while holding <c>gate</c>. Pass the lock that already guards the
/// resource and call <see cref="Touch"/> under it: a timer callback queued behind a use then
/// sees that use's new deadline and re-arms instead of releasing the resource just used.
/// </remarks>
internal sealed class IdleRelease : IDisposable
{
    private const long Disarmed = long.MaxValue;

    private readonly TimeSpan _idleDelay;
    private readonly Action _release;
    private readonly object _gate;
    private readonly Timer _timer;
    private long _deadline = Disarmed;

    internal IdleRelease(TimeSpan idleDelay, Action release, object gate)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleDelay, TimeSpan.Zero);
        _idleDelay = idleDelay;
        _release = release;
        _gate = gate;
        _timer = new Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Marks the resource as used now, restarting the idle countdown.</summary>
    internal void Touch()
    {
        lock (_gate)
        {
            _deadline = Environment.TickCount64 + (long)_idleDelay.TotalMilliseconds;
            _timer.Change(_idleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops a pending release without running it.</summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _deadline = Disarmed;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_deadline == Disarmed)
                return;

            long remaining = _deadline - Environment.TickCount64;
            if (remaining > 0)
            {
                _timer.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                return;
            }

            _deadline = Disarmed;
            _release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _deadline = Disarmed;
            _timer.Dispose();
        }
    }
}
