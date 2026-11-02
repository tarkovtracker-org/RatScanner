using System;
using System.Threading;
using System.Threading.Tasks;
using RatScanner.Scan;
using Xunit;

namespace RatScanner.Tests;

public sealed class ScanWarmupGateTests
{
    [Fact]
    public async Task Pending_foreground_stops_next_warmup_size_and_acquires_scan_lock()
    {
        ScanWarmupGate gate = new();
        object scanLock = new();
        using ManualResetEventSlim releaseFirstStep = new();
        using ManualResetEventSlim releaseForeground = new();
        TaskCompletionSource firstStepStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource foregroundPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource foregroundAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int steps = 0;
        Task<bool> warmup = Task.Factory.StartNew(
            () =>
            {
                Assert.True(
                    gate.TryRunBackgroundStep(
                        scanLock,
                        () =>
                        {
                            Assert.True(Monitor.IsEntered(scanLock));
                            Interlocked.Increment(ref steps);
                            firstStepStarted.SetResult();
                            releaseFirstStep.Wait(cancellation);
                            return true;
                        }
                    )
                );
                return gate.TryRunBackgroundStep(
                    scanLock,
                    () =>
                    {
                        Interlocked.Increment(ref steps);
                        return true;
                    }
                );
            },
            cancellation,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        Task foreground = Task.CompletedTask;
        try
        {
            await firstStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            foreground = Task.Factory.StartNew(
                () =>
                {
                    using IDisposable scope = gate.EnterForeground();
                    foregroundPending.SetResult();
                    lock (scanLock)
                        foregroundAcquired.SetResult();
                    // Keep intent visible until the background worker checks admission,
                    // independently of which waiter acquires the monitor first.
                    releaseForeground.Wait(cancellation);
                },
                cancellation,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            await foregroundPending.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            Assert.False(foregroundAcquired.Task.IsCompleted);
            releaseFirstStep.Set();
            Assert.False(await warmup.WaitAsync(TimeSpan.FromSeconds(5), cancellation));
            await foregroundAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            Assert.Equal(1, steps);
        }
        finally
        {
            releaseFirstStep.Set();
            releaseForeground.Set();
            await Task.WhenAll(warmup, foreground).WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        }
        Assert.True(gate.TryRunBackgroundStep(scanLock, () => true));
    }

    [Fact]
    public void Nested_foreground_scopes_release_once_and_keep_other_scopes_active()
    {
        ScanWarmupGate gate = new();
        object scanLock = new();
        IDisposable first = gate.EnterForeground();
        IDisposable second = gate.EnterForeground();
        Assert.False(
            gate.TryRunBackgroundStep(
                scanLock,
                () => throw new InvalidOperationException("Admitted while foreground active")
            )
        );
        first.Dispose();
        first.Dispose();
        Assert.False(gate.TryRunBackgroundStep(scanLock, () => true));
        second.Dispose();
        Assert.True(gate.TryRunBackgroundStep(scanLock, () => true));
        second.Dispose();
        Assert.True(gate.TryRunBackgroundStep(scanLock, () => true));
    }

    [Fact]
    public void Exceptions_release_foreground_scope_and_background_monitor()
    {
        ScanWarmupGate gate = new();
        object scanLock = new();
        void FailForeground()
        {
            using IDisposable scope = gate.EnterForeground();
            throw new InvalidOperationException("Foreground failed");
        }
        Assert.Throws<InvalidOperationException>(FailForeground);
        Assert.Throws<InvalidOperationException>(() =>
            gate.TryRunBackgroundStep(scanLock, () => throw new InvalidOperationException("Load failed"))
        );
        Assert.False(Monitor.IsEntered(scanLock));
        Assert.False(gate.TryRunBackgroundStep(scanLock, () => false));
        Assert.True(gate.TryRunBackgroundStep(scanLock, () => true));
    }
}
