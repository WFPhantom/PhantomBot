using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Services;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class NewAppMonitorService(ISteamCatalogClient steam, ITrackedAppStore store, INewAppNotifier notifier, IRetiredAppNotifier retiredNotifier, SteamBaselineCoordinator baselineCoordinator, IOptions<PhantomBotOptions> options, IHostApplicationLifetime applicationLifetime, ILogger<NewAppMonitorService> logger) : BackgroundService{
    private const int PendingMetadataBatchSize = 100;
    private readonly PhantomBotOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken){
        await store.InitializeAsync(stoppingToken);
        await steam.WaitUntilReadyAsync(stoppingToken);
        await EnsureBaselineAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        do{
            try{
                if (await RunPollingCycleAsync(stoppingToken)) continue;

                applicationLifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested){
                break;
            }
            catch (Exception exception){
                LogPollingFailed(logger, exception);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task EnsureBaselineAsync(CancellationToken cancellationToken){
        var checkpoint = await store.GetLastChangeNumberAsync(cancellationToken);
        var appCount = await store.CountAppsAsync(cancellationToken);

        if (appCount > 0){
            if (checkpoint is null) throw new InvalidDataException("The app database contains rows but has no Steam change-number checkpoint.");

            LogResuming(logger, checkpoint.Value);
            return;
        }

        if (checkpoint is null){
            checkpoint = await steam.GetCurrentChangeNumberAsync(cancellationToken);

            await store.SetLastChangeNumberAsync(checkpoint.Value, cancellationToken);
        }

        var request = await baselineCoordinator.GetOrCreateRequestAsync(checkpoint.Value, cancellationToken);

        LogBaselineRequired(logger, baselineCoordinator.RequestPath);

        var baseline = await baselineCoordinator.WaitForBaselineAsync(request, cancellationToken);

        await store.SeedAppsAsync(baseline.Apps, checkpoint.Value, cancellationToken);

        LogSeeded(logger, baseline.Apps.Count, checkpoint.Value);
    }

    internal async Task<bool> RunPollingCycleAsync(CancellationToken cancellationToken){
        var checkpoint = await store.GetLastChangeNumberAsync(cancellationToken) ?? throw new InvalidOperationException("Steam checkpoint is missing after initialization.");
        var changes = await steam.GetChangesSinceAsync(checkpoint, cancellationToken);

        if (changes.RequiresFullAppUpdate){
            LogContinuityLost(logger, checkpoint);
            return false;
        }

        await ProcessChangedAppsAsync(changes, cancellationToken);

        await store.SetLastChangeNumberAsync(changes.CurrentChangeNumber, cancellationToken);

        await RetryPendingMetadataAsync(cancellationToken);

        return true;
    }

    private async Task ProcessChangedAppsAsync(SteamChangeSet changes, CancellationToken cancellationToken){
        var changedIds = changes.AppChangeNumbers.Keys.ToArray();
        var tracked = await store.GetAppsAsync(changedIds, cancellationToken);
        var newIds = changedIds.Where(appId => !tracked.ContainsKey(appId)).ToArray();

        if (newIds.Length > 0){
            LogFirstSeenApps(logger, newIds.Length);

            await ProcessNewAppsAsync(newIds, changes.AppChangeNumbers, cancellationToken);
        }

        var appsToRefresh = tracked.Values.Where(static app => app.Status != TrackingStatus.Seeded).ToArray();

        if (appsToRefresh.Length > 0) await ResolveTrackedAppsAsync(appsToRefresh, cancellationToken);

        var changedSeededApps = tracked.Values.Where(static app => app.Status == TrackingStatus.Seeded).ToArray();

        if (changedSeededApps.Length > 0) await ResolveChangedSeededAppsAsync(changedSeededApps, cancellationToken);
    }

    private async Task ProcessNewAppsAsync(IReadOnlyCollection<uint> appIds, IReadOnlyDictionary<uint, uint> changeNumbers, CancellationToken cancellationToken){
        var metadata = await steam.GetAppMetadataAsync(appIds, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // ReSharper disable once ConvertClosureToMethodGroup
        foreach (var appId in appIds.OrderBy(id => changeNumbers.GetValueOrDefault(id))){
            var changeNumber = changeNumbers.GetValueOrDefault(appId);
            var app = metadata.GetValueOrDefault(appId) ?? new SteamAppMetadata(appId, null, SteamAppKind.Unknown, changeNumber);
            var isTerminalNonWanted = IsTerminalNonWantedKind(app.Kind);
            var isComplete = IsComplete(app);
            var status = isTerminalNonWanted ? TrackingStatus.Ignored : isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata;
            DateTimeOffset? nextCheck = status == TrackingStatus.PendingMetadata ? GetNextMetadataCheck(now) : null;
            ulong? messageId = null;

            if (!isTerminalNonWanted && (isComplete || _options.PostUnknownApps)) messageId = await notifier.PostAsync(app, cancellationToken);

            ulong? retirementMessageId = null;

            if (app.IsRetired){
                retirementMessageId = await retiredNotifier.PostAsync(app, cancellationToken);

                LogRetiredApp(logger, app.AppId, retirementMessageId.Value);
            }

            await store.UpsertAppAsync(CreateTracked(app, status, changeNumber, messageId, now, nextCheck, retirementMessageId), cancellationToken);
        }
    }

    private async Task RetryPendingMetadataAsync(CancellationToken cancellationToken){
        var pending = await store.GetPendingMetadataAsync(DateTimeOffset.UtcNow, PendingMetadataBatchSize, cancellationToken);

        if (pending.Count > 0) await ResolveTrackedAppsAsync(pending, cancellationToken);
    }

    private async Task ResolveChangedSeededAppsAsync(IReadOnlyCollection<TrackedSteamApp> trackedApps, CancellationToken cancellationToken){
        var picsMetadata = await steam.GetPicsAppMetadataAsync([.. trackedApps.Select(static app => app.AppId)], cancellationToken);
        var retiredIds = trackedApps.Where(app => picsMetadata.TryGetValue(app.AppId, out var metadata) && metadata.IsRetired).Select(static app => app.AppId).ToArray();
        IReadOnlyDictionary<uint, SteamAppMetadata> enrichedRetiredMetadata = new Dictionary<uint, SteamAppMetadata>();

        if (retiredIds.Length > 0) enrichedRetiredMetadata = await steam.GetAppMetadataAsync(retiredIds, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var tracked in trackedApps){
            if (!picsMetadata.TryGetValue(tracked.AppId, out var picsApp)) continue;

            if (picsApp.IsRetired){
                var retiredApp = picsApp;

                if (enrichedRetiredMetadata.TryGetValue(tracked.AppId, out var enrichedApp) && enrichedApp.IsRetired) retiredApp = enrichedApp;

                await ResolveRetiredAppAsync(tracked, retiredApp, now, cancellationToken);

                continue;
            }

            if (!tracked.IsRetired) continue;

            var restoredApp = picsApp with{
                Name = picsApp.Name ?? tracked.Name,
                Kind = picsApp.Kind == SteamAppKind.Unknown ? tracked.Kind : picsApp.Kind,
            };

            await ResolveHistoricalAppAsync(tracked, restoredApp, now, cancellationToken);
        }
    }

    private async Task ResolveTrackedAppsAsync(IReadOnlyCollection<TrackedSteamApp> trackedApps, CancellationToken cancellationToken){
        var metadata = await steam.GetAppMetadataAsync([.. trackedApps.Select(static app => app.AppId)], cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var tracked in trackedApps){
            metadata.TryGetValue(tracked.AppId, out var app);

            if (app?.IsRetired == true){
                await ResolveRetiredAppAsync(tracked, app, now, cancellationToken);

                continue;
            }

            var preserveHistoricalOrigin = tracked.Status == TrackingStatus.Seeded || (tracked.Status == TrackingStatus.SeededIncomplete && (app is null || app.ChangeNumber <= tracked.FirstSeenChange));

            if (preserveHistoricalOrigin){
                await ResolveHistoricalAppAsync(tracked, app, now, cancellationToken);

                continue;
            }

            await ResolveNewAppAsync(tracked, app, now, cancellationToken);
        }
    }

    private async Task ResolveHistoricalAppAsync(TrackedSteamApp tracked, SteamAppMetadata? app, DateTimeOffset now, CancellationToken cancellationToken){
        if (app is null){
            if (tracked.Status == TrackingStatus.Seeded) return;

            await store.UpsertAppAsync(tracked with{
                Status = TrackingStatus.SeededIncomplete,
                DiscordMessageId = null,
                UpdatedUtc = now,
                NextMetadataCheckUtc = GetNextMetadataCheck(now),
            }, cancellationToken);
            return;
        }

        var isResolved = IsTerminalNonWantedKind(app.Kind) || IsComplete(app);
        var status = tracked.Status == TrackingStatus.Seeded || isResolved ? TrackingStatus.Seeded : TrackingStatus.SeededIncomplete;

        await store.UpsertAppAsync(tracked with{
            Name = app.Name,
            Kind = app.Kind,
            Status = status,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = null,
            UpdatedUtc = now,
            NextMetadataCheckUtc = status == TrackingStatus.Seeded ? null : GetNextMetadataCheck(now),
            IsRetired = app.IsRetired,
        }, cancellationToken);
    }

    private async Task ResolveRetiredAppAsync(TrackedSteamApp tracked, SteamAppMetadata app, DateTimeOffset now, CancellationToken cancellationToken){
        var retiredApp = app with{
            Name = app.Name ?? tracked.Name,
            Kind = GetRetirementKind(tracked, app),
        };
        var status = tracked.Status == TrackingStatus.SeededIncomplete ? TrackingStatus.Seeded : tracked.Status;
        var messageId = tracked.DiscordMessageId;
        var shouldResolveNewApp = tracked.Status == TrackingStatus.PendingMetadata || (tracked.Status == TrackingStatus.SeededIncomplete && app.ChangeNumber > tracked.FirstSeenChange);

        if (shouldResolveNewApp){
            if (IsTerminalNonWantedKind(retiredApp.Kind)) status = TrackingStatus.Ignored;
            else{
                var isComplete = IsComplete(retiredApp);

                if (isComplete){
                    if (messageId is null) messageId = await notifier.PostAsync(retiredApp, cancellationToken);
                    else messageId = await notifier.UpdateAsync(messageId.Value, retiredApp, cancellationToken);
                }
                else if (_options.PostUnknownApps && messageId is null) messageId = await notifier.PostAsync(retiredApp, cancellationToken);

                status = isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata;
            }
        }

        var retirementMessageId = tracked.RetirementDiscordMessageId;

        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (!tracked.IsRetired && app.ChangeNumber > tracked.FirstSeenChange){
            if (retirementMessageId is null){
                var postedMessageId = await retiredNotifier.PostAsync(retiredApp, cancellationToken);

                retirementMessageId = postedMessageId;

                LogRetiredApp(logger, retiredApp.AppId, postedMessageId);
            }
            else if (app.ChangeNumber > tracked.LastSeenChange){
                var updatedMessageId = await retiredNotifier.UpdateAsync(retirementMessageId.Value, retiredApp, cancellationToken);

                retirementMessageId = updatedMessageId;

                LogUpdatedRetiredApp(logger, retiredApp.AppId, updatedMessageId);
            }
        }
        else if (tracked.IsRetired && retirementMessageId is not null && app.ChangeNumber > tracked.LastSeenChange){
            var updatedMessageId = await retiredNotifier.UpdateAsync(retirementMessageId.Value, retiredApp, cancellationToken);

            retirementMessageId = updatedMessageId;

            LogUpdatedRetiredApp(logger, retiredApp.AppId, updatedMessageId);
        }

        DateTimeOffset? nextCheck = status == TrackingStatus.PendingMetadata ? GetNextMetadataCheck(now) : null;

        await store.UpsertAppAsync(tracked with{
            Name = retiredApp.Name,
            Kind = retiredApp.Kind,
            Status = status,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = messageId,
            UpdatedUtc = now,
            NextMetadataCheckUtc = nextCheck,
            IsRetired = true,
            RetirementDiscordMessageId = retirementMessageId,
        }, cancellationToken);
    }

    private async Task ResolveNewAppAsync(TrackedSteamApp tracked, SteamAppMetadata? app, DateTimeOffset now, CancellationToken cancellationToken){
        if (app is null){
            await store.UpsertAppAsync(tracked with{
                Status = TrackingStatus.PendingMetadata,
                UpdatedUtc = now,
                NextMetadataCheckUtc = GetNextMetadataCheck(now),
            }, cancellationToken);
            return;
        }

        var messageId = tracked.DiscordMessageId;

        if (IsTerminalNonWantedKind(app.Kind)){
            await store.UpsertAppAsync(tracked with{
                Name = app.Name,
                Kind = app.Kind,
                Status = TrackingStatus.Ignored,
                LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
                DiscordMessageId = messageId,
                UpdatedUtc = now,
                NextMetadataCheckUtc = null,
                IsRetired = app.IsRetired,
            }, cancellationToken);
            return;
        }

        var isComplete = IsComplete(app);

        if (isComplete){
            if (messageId is null) messageId = await notifier.PostAsync(app, cancellationToken);
            else messageId = await notifier.UpdateAsync(messageId.Value, app, cancellationToken);
        }
        else if (_options.PostUnknownApps && messageId is null) messageId = await notifier.PostAsync(app, cancellationToken);

        await store.UpsertAppAsync(tracked with{
            Name = app.Name,
            Kind = app.Kind,
            Status = isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = messageId,
            UpdatedUtc = now,
            NextMetadataCheckUtc = isComplete ? null : GetNextMetadataCheck(now),
            IsRetired = app.IsRetired,
        }, cancellationToken);
    }

    private static SteamAppKind GetRetirementKind(TrackedSteamApp tracked, SteamAppMetadata app) => app.Kind == SteamAppKind.Unknown ? tracked.Kind : app.Kind;

    private static bool IsTerminalNonWantedKind(SteamAppKind kind) => kind is SteamAppKind.Other or SteamAppKind.Application;

    private static bool IsComplete(SteamAppMetadata app) => app.Kind.IsWanted() && !string.IsNullOrWhiteSpace(app.Name);

    private DateTimeOffset GetNextMetadataCheck(DateTimeOffset now) => now.AddSeconds(_options.MetadataRetrySeconds);

    private static TrackedSteamApp CreateTracked(SteamAppMetadata app, TrackingStatus status, uint changeNumber, ulong? messageId, DateTimeOffset now, DateTimeOffset? nextCheck, ulong? retirementMessageId) => new(){
        AppId = app.AppId,
        Name = app.Name,
        Kind = app.Kind,
        Status = status,
        FirstSeenChange = changeNumber,
        LastSeenChange = Math.Max(changeNumber, app.ChangeNumber),
        DiscordMessageId = messageId,
        FirstSeenUtc = now,
        UpdatedUtc = now,
        NextMetadataCheckUtc = nextCheck,
        IsRetired = app.IsRetired,
        RetirementDiscordMessageId = retirementMessageId,
    };

    [LoggerMessage(2000, LogLevel.Error, "Steam polling cycle failed; retrying on the next interval.")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception);
    [LoggerMessage(2001, LogLevel.Information, "Resuming from Steam change number {ChangeNumber}.")]
    private static partial void LogResuming(ILogger logger, uint changeNumber);
    [LoggerMessage(2002, LogLevel.Warning, "A one-time Steam baseline is required. In a second terminal, run the baseline utility using request file: {RequestPath}")]
    private static partial void LogBaselineRequired(ILogger logger, string requestPath);
    [LoggerMessage(2003, LogLevel.Information, "Seeded {AppCount} existing AppIDs at change number {ChangeNumber}; no historical notifications were sent.")]
    private static partial void LogSeeded(ILogger logger, int appCount, uint changeNumber);
    [LoggerMessage(2004, LogLevel.Information, "Found {Count} first-seen Steam AppID(s).")]
    private static partial void LogFirstSeenApps(ILogger logger, int count);
    [LoggerMessage(2005, LogLevel.Critical, "Steam can no longer replay changes from checkpoint {ChangeNumber}. PhantomBot is stopping rather than guessing. Restore a recent database backup before restarting.")]
    private static partial void LogContinuityLost(ILogger logger, uint changeNumber);
    [LoggerMessage(2006, LogLevel.Information, "Posted retirement notification for Steam AppID {AppId} as message {MessageId}.")]
    private static partial void LogRetiredApp(ILogger logger, uint appId, ulong messageId);
    [LoggerMessage(2007, LogLevel.Information, "Updated retirement notification for Steam AppID {AppId} as message {MessageId}.")]
    private static partial void LogUpdatedRetiredApp(ILogger logger, uint appId, ulong messageId);
}