#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using RatScanner.FetchModels.TarkovTracker;
using RatScanner.Runtime;
using RatScanner.ViewModel;
using Xunit;

namespace RatScanner.Tests;

// CA1711: xUnit convention names collection-definition classes "*Collection".
#pragma warning disable CA1711
[CollectionDefinition(nameof(ApplicationBoundaryTests), DisableParallelization = true)]
public sealed class ApplicationBoundaryTestCollection;
#pragma warning restore CA1711

[Collection(nameof(ApplicationBoundaryTests))]
public sealed class ApplicationBoundaryTests
{
    [Fact]
    public async Task Scanner_settings_apply_runtime_changes_through_injected_contracts()
    {
        bool originalNameScan = RatConfig.NameScan.Enable;
        bool originalIconScan = RatConfig.IconScan.Enable;
        bool originalUseCachedIcons = RatConfig.IconScan.UseCachedIcons;
        FakeScanOrchestrator scans = new();
        FakeHotkeyRegistrar hotkeys = new();
        using SettingsPersistenceService persistence = new(_ => Task.CompletedTask);
        using SettingsVM settings = new(
            new LocalizationService(),
            persistence,
            scans,
            new FakeTrackerService(),
            hotkeys
        );

        try
        {
            SettingSaveResult nameResult = await settings.SetEnableNameScanAsync(!originalNameScan);
            SettingSaveResult iconResult = await settings.SetEnableIconScanAsync(!originalIconScan);
            SettingSaveResult cacheResult = await settings.SetUseCachedIconsAsync(!originalUseCachedIcons);

            Assert.True(nameResult.Succeeded);
            Assert.True(iconResult.Succeeded);
            Assert.True(cacheResult.Succeeded);
            Assert.Equal(2, hotkeys.RegistrationCount);
            Assert.Equal(1, scans.RebuildCount);
        }
        finally
        {
            RatConfig.NameScan.Enable = originalNameScan;
            RatConfig.IconScan.Enable = originalIconScan;
            RatConfig.IconScan.UseCachedIcons = originalUseCachedIcons;
        }
    }

    [Fact]
    public async Task Tooltip_capture_exclusion_can_be_turned_off_and_back_on_from_settings()
    {
        bool original = RatConfig.ToolTip.ExcludeFromCapture;
        int persisted = 0;
        using SettingsPersistenceService persistence = new(_ =>
        {
            persisted++;
            return Task.CompletedTask;
        });
        using SettingsVM settings = new(
            new LocalizationService(),
            persistence,
            new FakeScanOrchestrator(),
            new FakeTrackerService(),
            new FakeHotkeyRegistrar()
        );

        try
        {
            RatConfig.ToolTip.ExcludeFromCapture = true;

            SettingSaveResult offResult = await settings.SetExcludeTooltipFromCaptureAsync(false);
            Assert.True(offResult.Succeeded);
            Assert.False(RatConfig.ToolTip.ExcludeFromCapture);

            SettingSaveResult onResult = await settings.SetExcludeTooltipFromCaptureAsync(true);
            Assert.True(onResult.Succeeded);
            Assert.True(RatConfig.ToolTip.ExcludeFromCapture);
            Assert.Equal(2, persisted);
        }
        finally
        {
            RatConfig.ToolTip.ExcludeFromCapture = original;
        }
    }

    [Fact]
    public void Menu_view_model_forwards_scan_changes_from_injected_orchestrator()
    {
        FakeScanOrchestrator scans = new();
        MenuVM menu = new(scans, new FakeTrackerService());
        int notifications = 0;
        menu.PropertyChanged += (_, _) => notifications++;

        scans.NotifyChanged();

        Assert.Same(scans.ItemScans, menu.ItemScans);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Tracker_snapshot_resolves_the_current_user_from_one_consistent_state()
    {
        UserProgress teammate = new() { UserId = "team" };
        UserProgress currentUser = new() { UserId = "self" };
        TrackerStateSnapshot snapshot = new(
            new List<UserProgress> { teammate, currentUser },
            "self",
            "token",
            TrackerConnectionState.Connected
        );

        Assert.Same(currentUser, snapshot.CurrentUser);
        Assert.Equal("token", snapshot.Token);
        Assert.Equal(TrackerConnectionState.Connected, snapshot.ConnectionState);
    }

    private sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
    {
        public int RegistrationCount { get; private set; }

        public void RegisterHotkeys() => RegistrationCount++;
    }
}
