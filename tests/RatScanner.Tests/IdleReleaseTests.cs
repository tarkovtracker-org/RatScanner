using System;
using System.Threading;
using RatScanner.Display;
using Xunit;

namespace RatScanner.Tests;

public sealed class IdleReleaseTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

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
            }
        );

        idle.Touch();

        Assert.True(released.Wait(Generous));
        Thread.Sleep(Idle * 2);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Each_use_restarts_the_countdown()
    {
        int count = 0;
        using IdleRelease idle = new(TimeSpan.FromSeconds(1), () => Interlocked.Increment(ref count));

        for (int i = 0; i < 6; i++)
        {
            idle.Touch();
            Thread.Sleep(100);
        }

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void Nothing_is_released_before_first_use_or_after_cancel()
    {
        int count = 0;
        using IdleRelease idle = new(Idle, () => Interlocked.Increment(ref count));
        Thread.Sleep(Idle * 2);

        idle.Touch();
        idle.Cancel();
        Thread.Sleep(Idle * 3);

        Assert.Equal(0, Volatile.Read(ref count));
    }
}
