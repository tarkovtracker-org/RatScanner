using System;
using System.Threading;
using System.Threading.Tasks;

namespace RatScanner;

internal static class CredentialPersistenceCoordinator
{
    internal static async Task<SettingSaveResult> PersistCandidateAsync(
        Func<Task<SettingSaveResult>> persistCandidate,
        Func<Task<SettingSaveResult>> restorePrevious,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(persistCandidate);
        ArgumentNullException.ThrowIfNull(restorePrevious);

        SettingSaveResult result = await persistCandidate().ConfigureAwait(false);
        if (!result.Succeeded)
            return result;

        await RestoreIfCanceledAsync(restorePrevious, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Restores the previous credential and throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is already canceled; otherwise returns.
    /// The check inside <see cref="PersistCandidateAsync"/> runs off the caller's context, so a
    /// dismissal that lands while the caller's continuation is queued is invisible to it.
    /// Callers must repeat this once they resume on their own dispatcher, before treating
    /// the candidate as committed.
    /// </summary>
    internal static async Task RestoreIfCanceledAsync(
        Func<Task<SettingSaveResult>> restorePrevious,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(restorePrevious);
        if (!cancellationToken.IsCancellationRequested)
            return;

        try
        {
            SettingSaveResult restored = await restorePrevious().ConfigureAwait(false);
            if (!restored.Succeeded)
                Logger.LogWarning("Unable to restore a credential after the replacement was canceled.");
        }
        catch (Exception exception)
        {
            // The cancellation must still propagate; log rollback failures
            // instead of masking the OperationCanceledException.
            Logger.LogWarning("Unable to restore a credential after the replacement was canceled.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
