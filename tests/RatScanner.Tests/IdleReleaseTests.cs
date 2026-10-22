using System;
using System.Diagnostics;
using System.Threading;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

public sealed class IdleReleaseTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    // System.Threading.Timer may fire up to one OS timer tick early.
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(40);

    [Fact]
    public void Releases_once_after_the_idle_period()
    {
        using ManualResetEventSlim released = new();
        int count = 0;
        using IdleRelease idle = new(
            Idle,
            () =>
            {
                Interlocked.Increment(ref count);
                released.Set();
            },
            new object()
        );

        idle.Touch();

        Assert.True(released.Wait(Generous, TestContext.Current.CancellationToken));
        Thread.Sleep(Idle * 2);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Each_use_restarts_the_countdown()
    {
        TimeSpan idleDelay = TimeSpan.FromMilliseconds(500);
        using ManualResetEventSlim released = new();
        using IdleRelease idle = new(idleDelay, released.Set, new object());

        idle.Touch();
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(100);
            Assert.False(released.IsSet);
            idle.Touch();
        }
        Stopwatch sinceLastUse = Stopwatch.StartNew();

        Assert.True(released.Wait(Generous, TestContext.Current.CancellationToken));
        Assert.True(sinceLastUse.Elapsed >= idleDelay - TimerSlack, $"Released after {sinceLastUse.Elapsed}.");
    }

    [Fact]
    public void A_release_queued_behind_a_use_waits_for_the_new_idle_period()
    {
        object gate = new();
        using ManualResetEventSlim released = new();
        using IdleRelease idle = new(Idle, released.Set, gate);
        Stopwatch sinceLastUse;

        idle.Touch();
        lock (gate)
        {
            // The first deadline passes while a use holds the gate, so its callback queues.
            Thread.Sleep(Idle * 3);
            idle.Touch();
            sinceLastUse = Stopwatch.StartNew();
        }

        Assert.True(released.Wait(Generous, TestContext.Current.CancellationToken));
        Assert.True(sinceLastUse.Elapsed >= Idle - TimerSlack, $"Released after {sinceLastUse.Elapsed}.");
    }

    [Fact]
    public void Nothing_is_released_before_first_use_or_after_cancel()
    {
        using ManualResetEventSlim released = new();
        using IdleRelease idle = new(Idle, released.Set, new object());
        Assert.False(released.Wait(Idle * 2, TestContext.Current.CancellationToken));

        idle.Touch();
        idle.Cancel();

        Assert.False(released.Wait(Idle * 3, TestContext.Current.CancellationToken));
    }
}
