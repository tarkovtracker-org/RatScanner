using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using RatScanner.Components;
using RatScanner.Pages.App.Settings;
using RatScanner.Runtime;
using RatScanner.ViewModel;
using Xunit;
using GameMode = RatScanner.TarkovDev.GameMode;

namespace RatScanner.Tests;

[Collection(RatConfigCollection.Name)]
public sealed class TrackerComponentLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dialog_dismissal_cancels_validation_and_safely_drains_its_continuation(
        bool cancelFirst,
        bool canceledResponse
    )
    {
        Dispatcher dispatcher = Dispatcher.CreateDefault();
        using ChangeConnectionDialog component = new();
        PendingTrackerService tracker = new();
        IMudDialogInstance dialog = DispatchProxy.Create<IMudDialogInstance, DialogRecorder>();
        DialogRecorder recorder = (DialogRecorder)dialog;
        SetProperty(component, "TrackerService", tracker);
        SetProperty(component, "DialogInstance", dialog);
        SetField(component, "_tokenDraft", " test-candidate ");
        Task submission = dispatcher.InvokeAsync(() => InvokeAsync(component, "SubmitAsync"));
        CancellationTokenSource source = GetField<CancellationTokenSource>(component, "_validation");
        PendingValidation validation = tracker.Validations[GameMode.Regular];
        Assert.False(submission.IsCompleted);
        Assert.Equal("test-candidate", validation.Candidate);
        Assert.True(GetField<bool>(component, "_testing"));
        using CancellationTokenRegistration reentrantDisposal = validation.Token.Register(component.Dispose);

        await dispatcher.InvokeAsync(() =>
        {
            if (cancelFirst)
                Invoke(component, "Cancel");
            component.Dispose();
            component.Dispose();
        });

        Assert.Null(GetField<CancellationTokenSource>(component, "_validation"));
        Assert.True(validation.Token.IsCancellationRequested);
        Assert.Equal(cancelFirst ? 1 : 0, recorder.CancelCount);
        Complete(validation, canceledResponse);
        await submission.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, recorder.CloseCount);
        Assert.Equal(0, tracker.ActivationCount);
        Assert.Null(GetField<string>(component, "_error"));
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_disposal_cancels_every_mode_and_safely_drains_pending_validations(bool canceledResponse)
    {
        Dispatcher dispatcher = Dispatcher.CreateDefault();
        using SettingsTracking component = new();
        PendingTrackerService tracker = new();
        SetProperty(component, "TrackerService", tracker);
        Dictionary<GameMode, string> drafts = GetField<Dictionary<GameMode, string>>(component, "_draft");
        Dictionary<GameMode, CancellationTokenSource> sources = GetField<Dictionary<GameMode, CancellationTokenSource>>(
            component,
            "_validation"
        );
        List<Task> submissions = new();
        List<CancellationTokenSource> ownedSources = new();
        foreach (GameMode mode in new[] { GameMode.Regular, GameMode.Pve, GameMode.Seasonal })
        {
            drafts[mode] = "test-candidate";
            submissions.Add(dispatcher.InvokeAsync(() => InvokeAsync(component, "ConnectAsync", mode)));
            ownedSources.Add(sources[mode]);
        }

        Assert.All(submissions, submission => Assert.False(submission.IsCompleted));
        await dispatcher.InvokeAsync(() =>
        {
            component.Dispose();
            component.Dispose();
        });
        Assert.All(sources.Values, source => Assert.Null(source));
        foreach (PendingValidation validation in tracker.Validations.Values)
        {
            Assert.True(validation.Token.IsCancellationRequested);
            Complete(validation, canceledResponse);
        }
        await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, tracker.ActivationCount);
        Assert.All(GetField<Dictionary<GameMode, string>>(component, "_error").Values, error => Assert.Null(error));
        Assert.All(ownedSources, source => Assert.Throws<ObjectDisposedException>(() => source.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dismissal_during_activation_does_not_publish_success(bool useDialog)
    {
        Dispatcher dispatcher = Dispatcher.CreateDefault();
        GameMode mode = RatConfig.GameMode;
        string originalToken = RatConfig.Tracking.TarkovTracker.TokenForMode(mode);
        PendingTrackerService tracker = new();
        using SettingsPersistenceService persistence = new(_ => Task.CompletedTask);
        using SettingsVM settings = new(
            new LocalizationService(),
            persistence,
            new FakeScanOrchestrator(),
            tracker,
            new NoopHotkeyRegistrar()
        );
        using IDisposable component = useDialog ? new ChangeConnectionDialog() : new SettingsTracking();
        IMudDialogInstance dialog = DispatchProxy.Create<IMudDialogInstance, DialogRecorder>();
        SetProperty(component, "TrackerService", tracker);
        SetProperty(component, "SettingsVM", settings);
        if (useDialog)
        {
            component.GetType().GetProperty("Mode").SetValue(component, mode);
            SetProperty(component, "DialogInstance", dialog);
            SetField(component, "_tokenDraft", "test-candidate");
        }
        else
        {
            GetField<Dictionary<GameMode, string>>(component, "_draft")[mode] = "test-candidate";
        }

        try
        {
            Task submission = dispatcher.InvokeAsync(() =>
                useDialog ? InvokeAsync(component, "SubmitAsync") : InvokeAsync(component, "ConnectAsync", mode)
            );
            tracker.Validations[mode].Completion.SetResult(TrackerValidationResult.Success);
            await tracker.ActivationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
            Assert.Equal("test-candidate", RatConfig.Tracking.TarkovTracker.TokenForMode(mode));
            await dispatcher.InvokeAsync(component.Dispose);
            Assert.True(tracker.Validations[mode].Token.IsCancellationRequested);
            // The real tracker can return normally after swallowing progress cancellation.
            tracker.ActivationCompletion.SetResult();
            await submission.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(1, tracker.ActivationCount);
            Assert.Equal(0, ((DialogRecorder)dialog).CloseCount);
            Assert.Equal(0, tracker.StateReadCount);
            if (!useDialog)
                Assert.Equal("test-candidate", GetField<Dictionary<GameMode, string>>(component, "_draft")[mode]);
        }
        finally
        {
            tracker.ActivationCompletion.TrySetResult();
            RatConfig.Tracking.TarkovTracker.SetTokenForMode(mode, originalToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dismissal_before_persistence_continuation_does_not_activate_or_publish_success(
        bool useDialog,
        bool activeMode
    )
    {
        Dispatcher dispatcher = Dispatcher.CreateDefault();
        GameMode mode =
            activeMode ? RatConfig.GameMode
            : RatConfig.GameMode == GameMode.Regular ? GameMode.Pve
            : GameMode.Regular;
        string originalToken = RatConfig.Tracking.TarkovTracker.TokenForMode(mode);
        using IDisposable component = useDialog ? new ChangeConnectionDialog() : new SettingsTracking();
        DismissalContext context = null;
        PendingTrackerService tracker = new();
        tracker.ActivationCompletion.SetResult();
        TaskCompletionSource persistenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource persistenceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SettingsPersistenceService persistence = new(async _ =>
        {
            persistenceStarted.TrySetResult();
            await persistenceCompletion.Task.ConfigureAwait(false);
            context.Arm();
        });
        using SettingsVM settings = new(
            new LocalizationService(),
            persistence,
            new FakeScanOrchestrator(),
            tracker,
            new NoopHotkeyRegistrar()
        );
        IMudDialogInstance dialog = DispatchProxy.Create<IMudDialogInstance, DialogRecorder>();
        SetProperty(component, "TrackerService", tracker);
        SetProperty(component, "SettingsVM", settings);
        if (useDialog)
        {
            component.GetType().GetProperty("Mode").SetValue(component, mode);
            SetProperty(component, "DialogInstance", dialog);
            SetField(component, "_tokenDraft", "test-candidate");
        }
        else
        {
            GetField<Dictionary<GameMode, string>>(component, "_draft")[mode] = "test-candidate";
        }

        try
        {
            Task submission = dispatcher.InvokeAsync(() =>
            {
                SynchronizationContext originalContext = SynchronizationContext.Current;
                context = new DismissalContext(originalContext, component);
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    return useDialog
                        ? InvokeAsync(component, "SubmitAsync")
                        : InvokeAsync(component, "ConnectAsync", mode);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(originalContext);
                }
            });
            tracker.Validations[mode].Completion.SetResult(TrackerValidationResult.Success);
            await persistenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // Queue behind validation's continuation so persistence cannot complete
            // synchronously before the component has registered its await.
            await dispatcher.InvokeAsync(() => persistenceCompletion.SetResult());
            await submission.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(1, context.DismissalCount);
            Assert.True(tracker.Validations[mode].Token.IsCancellationRequested);
            Assert.Equal(0, tracker.ActivationCount);
            Assert.Equal(0, ((DialogRecorder)dialog).CloseCount);
            Assert.Equal(0, tracker.StateReadCount);
            if (!useDialog)
            {
                Assert.Equal("test-candidate", GetField<Dictionary<GameMode, string>>(component, "_draft")[mode]);
                Assert.Equal(
                    TrackerConnectionState.Testing,
                    GetField<Dictionary<GameMode, TrackerConnectionState>>(component, "_state")[mode]
                );
            }
        }
        finally
        {
            persistenceCompletion.TrySetResult();
            RatConfig.Tracking.TarkovTracker.SetTokenForMode(mode, originalToken);
        }
    }

    // Deliver dismissal on the real dispatcher immediately before the continuation
    // queued by successful persistence. This covers the gap after the coordinator's
    // off-dispatcher cancellation check without sleeps or a live credential store.
    private sealed class DismissalContext(SynchronizationContext inner, IDisposable component) : SynchronizationContext
    {
        private int _armed;
        internal int DismissalCount { get; private set; }

        internal void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override void Post(SendOrPostCallback callback, object state) =>
            inner.Post(
                value =>
                {
                    if (Interlocked.Exchange(ref _armed, 0) == 1)
                    {
                        component.Dispose();
                        DismissalCount++;
                    }
                    SynchronizationContext previous = Current;
                    SetSynchronizationContext(this);
                    try
                    {
                        callback(value);
                    }
                    finally
                    {
                        SetSynchronizationContext(previous);
                    }
                },
                state
            );
    }

    private sealed class NoopHotkeyRegistrar : IHotkeyRegistrar
    {
        public void RegisterHotkeys() => throw new InvalidOperationException("No hotkeys should be registered.");
    }

    private static void Complete(PendingValidation validation, bool canceledResponse)
    {
        if (canceledResponse)
            validation.Completion.SetCanceled(validation.Token);
        else
            validation.Completion.SetResult(TrackerValidationResult.Success);
    }

    // Exercise the compiled components' real handlers on a Blazor dispatcher without
    // rendering Mud controls or requiring a live tracker. Reflection is confined to
    // this lifecycle harness rather than adding public test hooks to product code.
    private static object Invoke(object component, string method, params object[] arguments) =>
        component
            .GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(component, arguments);

    private static Task InvokeAsync(object component, string method, params object[] arguments) =>
        (Task)Invoke(component, method, arguments);

    private static T GetField<T>(object component, string name) =>
        (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);

    private static void SetField(object component, string name, object value) =>
        component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(component, value);

    private static void SetProperty(object component, string name, object value) =>
        component
            .GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(component, value);

    private sealed record PendingValidation(string Candidate, CancellationToken Token)
    {
        internal TaskCompletionSource<TrackerValidationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingTrackerService : ITrackerService
    {
        internal Dictionary<GameMode, PendingValidation> Validations { get; } = new();
        internal int ActivationCount { get; private set; }
        internal int StateReadCount { get; private set; }
        internal TaskCompletionSource ActivationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ActivationCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrackerStateSnapshot State
        {
            get
            {
                StateReadCount++;
                return new([], "", null, TrackerConnectionState.Connected);
            }
        }

        public Task ActivateModeAsync(GameMode mode, CancellationToken cancellationToken = default)
        {
            ActivationCount++;
            ActivationStarted.SetResult();
            return ActivationCompletion.Task;
        }

        public Task<TrackerValidationResult> ValidateOrgKeyAsync(
            GameMode mode,
            string token,
            CancellationToken cancellationToken = default
        )
        {
            PendingValidation validation = new(token, cancellationToken);
            Validations.Add(mode, validation);
            return validation.Completion.Task;
        }
    }

    public class DialogRecorder : DispatchProxy
    {
        public int CancelCount { get; private set; }
        public int CloseCount { get; private set; }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod.Name)
            {
                case nameof(IMudDialogInstance.Cancel):
                    CancelCount++;
                    return null;
                case nameof(IMudDialogInstance.Close):
                    CloseCount++;
                    return null;
                default:
                    throw new InvalidOperationException($"Unexpected dialog call: {targetMethod.Name}");
            }
        }
    }
}
