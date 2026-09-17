using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RatScanner.Tests;

public sealed class CredentialPersistenceCoordinatorTests
{
    [Fact]
    public async Task Cancellation_after_candidate_save_restores_the_previous_value()
    {
        bool candidateSaved = false;
        bool previousRestored = false;
        using CancellationTokenSource cancellation = new();

        Task<SettingSaveResult> operation = CredentialPersistenceCoordinator.PersistCandidateAsync(
            () =>
            {
                candidateSaved = true;
                cancellation.Cancel();
                return Task.FromResult(new SettingSaveResult(true));
            },
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            cancellation.Token
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.True(candidateSaved);
        Assert.True(previousRestored);
    }

    [Fact]
    public async Task Cancellation_still_propagates_when_restore_throws()
    {
        bool restoreAttempted = false;
        using CancellationTokenSource cancellation = new();

        Task<SettingSaveResult> operation = CredentialPersistenceCoordinator.PersistCandidateAsync(
            () =>
            {
                cancellation.Cancel();
                return Task.FromResult(new SettingSaveResult(true));
            },
            () =>
            {
                restoreAttempted = true;
                throw new InvalidOperationException("restore failed");
            },
            cancellation.Token
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.True(restoreAttempted);
    }

    [Fact]
    public async Task Late_cancellation_check_restores_the_previous_value_then_throws()
    {
        bool previousRestored = false;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Task operation = CredentialPersistenceCoordinator.RestoreIfCanceledAsync(
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            cancellation.Token
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.True(previousRestored);
    }

    [Fact]
    public async Task Late_cancellation_check_still_throws_when_restore_throws()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Task operation = CredentialPersistenceCoordinator.RestoreIfCanceledAsync(
            () => throw new InvalidOperationException("restore failed"),
            cancellation.Token
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Late_cancellation_check_is_a_no_op_without_cancellation()
    {
        bool previousRestored = false;

        await CredentialPersistenceCoordinator.RestoreIfCanceledAsync(
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            CancellationToken.None
        );

        Assert.False(previousRestored);
    }

    [Fact]
    public async Task Failed_candidate_save_does_not_restore_or_throw()
    {
        bool previousRestored = false;

        SettingSaveResult result = await CredentialPersistenceCoordinator.PersistCandidateAsync(
            () => Task.FromResult(new SettingSaveResult(false)),
            () =>
            {
                previousRestored = true;
                return Task.FromResult(new SettingSaveResult(true));
            },
            CancellationToken.None
        );

        Assert.False(result.Succeeded);
        Assert.False(previousRestored);
    }
}
