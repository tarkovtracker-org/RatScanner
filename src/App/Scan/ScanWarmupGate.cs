using System;
using System.Threading;

namespace RatScanner.Scan;

/// <summary>Stops background warm-up admission while a foreground scan is pending or active.</summary>
internal sealed class ScanWarmupGate
{
    private int _foregroundCount;

    internal IDisposable EnterForeground()
    {
        Interlocked.Increment(ref _foregroundCount);
        return new ForegroundScope(this);
    }

    internal bool TryRunBackgroundStep(object scanLock, Func<bool> step)
    {
        lock (scanLock)
        {
            if (Volatile.Read(ref _foregroundCount) != 0)
                return false;
            return step();
        }
    }

    private sealed class ForegroundScope(ScanWarmupGate owner) : IDisposable
    {
        private ScanWarmupGate? _owner = owner;

        public void Dispose()
        {
            ScanWarmupGate? gate = Interlocked.Exchange(ref _owner, null);
            if (gate is not null)
                Interlocked.Decrement(ref gate._foregroundCount);
        }
    }
}
