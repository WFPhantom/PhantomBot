using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Services;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class NewAppMonitorService(ISteamCatalogClient steam, ITrackedAppStore store, INewAppNotifier notifier, IRetiredAppNotifier retiredNotifier, SteamBaselineCoordinator baselineCoordinator, IOptions<PhantomBotOptions> options, IHostApplicationLifetime applicationLifetime, ILogger<NewAppMonitorService> logger) : BackgroundService{
    private const int PendingMetadataBatchSize = 100;
    private const int RetirementBackfillBatchSize = 100;
    private const int RecoveryBatchSize = 500;
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContinuityRetryDelay = TimeSpan.FromMinutes(5);
    private readonly PhantomBotOptions _options = options.Value;
    private SteamBaselineRequest? _recoveryRequest;
    private uint _retirementBackfillCursor;
    internal bool RetirementTrackingEnabled{ get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken){
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, applicationLifetime.ApplicationStopping);
        var cancellationToken = shutdown.Token;
        var initialized = false;
        var fullUpdateResponses = 0;

        try{
            while (!cancellationToken.IsCancellationRequested){
                try{
                    if (!initialized){
                        await store.InitializeAsync(cancellationToken);
                        await steam.WaitUntilReadyAsync(cancellationToken);
                        await EnsureBaselineAsync(cancellationToken);

                        initialized = true;
                    }

                    if (_recoveryRequest is not null){
                        await RecoverContinuityAsync(cancellationToken);

                        fullUpdateResponses = 0;
                    }
                    else if (await RunPollingCycleAsync(cancellationToken)) fullUpdateResponses = 0;
                    else{
                        fullUpdateResponses++;

                        if (fullUpdateResponses < 2){
                            await Task.Delay(ContinuityRetryDelay, cancellationToken);

                            continue;
                        }

                        await RecoverContinuityAsync(cancellationToken);

                        fullUpdateResponses = 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
                    break;
                }
                catch (Exception exception){
                    LogPollingFailed(logger, exception);

                    await Task.Delay(FailureRetryDelay, cancellationToken);

                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
            // Normal shutdown, including cancellation during a retry delay.
        }
    }

    private async Task EnsureBaselineAsync(CancellationToken cancellationToken){
        var checkpoint = await store.GetLastChangeNumberAsync(cancellationToken);
        var appCount = await store.CountAppsAsync(cancellationToken);

        if (appCount > 0){
            if (checkpoint is null) throw new InvalidDataException("The app database contains rows but has no Steam change-number checkpoint.");

            var currentChangeNumber = await steam.GetCurrentChangeNumberAsync(cancellationToken);

            _recoveryRequest = await TryGetReplayableRecoveryRequestAsync(checkpoint.Value, currentChangeNumber, cancellationToken);

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
        if (changes.CurrentChangeNumber < checkpoint) throw new InvalidDataException("Steam returned a change number older than the saved checkpoint. The checkpoint has not been changed.");

        if (changes.RequiresFullAppUpdate){
            LogContinuityLost(logger, checkpoint);
            return false;
        }

        await ProcessChangedAppsAsync(changes, cancellationToken);

        await store.SetLastChangeNumberAsync(changes.CurrentChangeNumber, cancellationToken);

        await RetryPendingMetadataAsync(cancellationToken);

        if (RetirementTrackingEnabled) await BackfillRetirementStatesAsync(cancellationToken);

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

        foreach (var appId in appIds.OrderBy(changeNumbers.GetValueOrDefault)){
            var changeNumber = changeNumbers.GetValueOrDefault(appId);

            metadata.TryGetValue(appId, out var app);

            var retirementObservedChange = app?.ChangeNumber;

            app ??= new SteamAppMetadata(appId, null, SteamAppKind.Unknown, changeNumber);

            var isTerminalNonWanted = IsTerminalNonWantedKind(app.Kind);
            var isComplete = IsComplete(app);
            var status = isTerminalNonWanted ? TrackingStatus.Ignored : isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata;
            DateTimeOffset? nextCheck = status == TrackingStatus.PendingMetadata ? GetNextMetadataCheck(now) : null;
            ulong? messageId = null;

            if (!isTerminalNonWanted && (isComplete || _options.PostUnknownApps)) messageId = await notifier.PostAsync(app, cancellationToken);

            ulong? retirementMessageId = null;

            if (RetirementTrackingEnabled && retirementObservedChange is not null && app.IsRetired){
                retirementMessageId = await retiredNotifier.PostAsync(app, cancellationToken);

                LogRetiredApp(logger, app.AppId, retirementMessageId.Value);
            }

            await store.UpsertAppAsync(CreateTracked(app, status, changeNumber, messageId, now, nextCheck, retirementMessageId, retirementObservedChange), cancellationToken);
        }
    }

    private async Task RetryPendingMetadataAsync(CancellationToken cancellationToken){
        var pending = await store.GetPendingMetadataAsync(DateTimeOffset.UtcNow, PendingMetadataBatchSize, cancellationToken);

        if (pending.Count > 0) await ResolveTrackedAppsAsync(pending, cancellationToken);
    }

    private async Task ResolveChangedSeededAppsAsync(IReadOnlyCollection<TrackedSteamApp> trackedApps, CancellationToken cancellationToken){
        var picsMetadata = await steam.GetPicsAppMetadataAsync([.. trackedApps.Select(static app => app.AppId)], cancellationToken);
        var retiredIds = RetirementTrackingEnabled ? trackedApps.Where(tracked => picsMetadata.TryGetValue(tracked.AppId, out var app) && !ShouldIgnoreRetirementObservation(tracked, app) && CanReportRetirement(tracked, app)).Select(static app => app.AppId).ToArray() : [];
        IReadOnlyDictionary<uint, SteamAppMetadata> enrichedMetadata = new Dictionary<uint, SteamAppMetadata>();

        if (retiredIds.Length > 0) enrichedMetadata = await steam.GetAppMetadataAsync(retiredIds, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var tracked in trackedApps){
            if (!picsMetadata.TryGetValue(tracked.AppId, out var app)) continue;

            if (enrichedMetadata.TryGetValue(tracked.AppId, out var enrichedApp) && enrichedApp.ChangeNumber >= app.ChangeNumber) app = enrichedApp;

            if (ShouldIgnoreRetirementObservation(tracked, app)) continue;

            if (app.IsRetired){
                await ResolveRetiredAppAsync(tracked, app, now, cancellationToken);

                continue;
            }

            if (tracked.IsRetired){
                var restoredApp = app with{
                    Name = app.Name ?? tracked.Name,
                    Kind = app.Kind == SteamAppKind.Unknown ? tracked.Kind : app.Kind,
                };

                await ResolveHistoricalAppAsync(tracked, restoredApp, now, cancellationToken);

                continue;
            }

            var observed = ApplyRetirementObservation(tracked, app);

            if (observed.RetirementObservedChange != tracked.RetirementObservedChange) await store.UpsertAppAsync(observed with{ UpdatedUtc = now }, cancellationToken);
        }
    }

    private async Task ResolveTrackedAppsAsync(IReadOnlyCollection<TrackedSteamApp> trackedApps, CancellationToken cancellationToken){
        var metadata = await steam.GetAppMetadataAsync([.. trackedApps.Select(static app => app.AppId)], cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var tracked in trackedApps){
            metadata.TryGetValue(tracked.AppId, out var app);

            if (app is not null && ShouldIgnoreRetirementObservation(tracked, app)){
                if (tracked.Status is TrackingStatus.PendingMetadata or TrackingStatus.SeededIncomplete){
                    await store.UpsertAppAsync(tracked with{
                        UpdatedUtc = now,
                        NextMetadataCheckUtc = GetNextMetadataCheck(now),
                    }, cancellationToken);
                }

                continue;
            }

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

        if (ShouldIgnoreRetirementObservation(tracked, app)) return;

        var isResolved = IsTerminalNonWantedKind(app.Kind) || IsComplete(app);
        var status = tracked.Status == TrackingStatus.Seeded || isResolved ? TrackingStatus.Seeded : TrackingStatus.SeededIncomplete;

        var updated = tracked with{
            Name = app.Name,
            Kind = app.Kind,
            Status = status,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = null,
            UpdatedUtc = now,
            NextMetadataCheckUtc = status == TrackingStatus.Seeded ? null : GetNextMetadataCheck(now),
        };

        await store.UpsertAppAsync(ApplyRetirementObservation(updated, app), cancellationToken);
    }

    private async Task ResolveRetiredAppAsync(TrackedSteamApp tracked, SteamAppMetadata app, DateTimeOffset now, CancellationToken cancellationToken){
        if (ShouldIgnoreRetirementObservation(tracked, app)) return;

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

                if (isComplete) messageId = messageId is null ? await notifier.PostAsync(retiredApp, cancellationToken) : await notifier.UpdateAsync(messageId.Value, retiredApp, cancellationToken);
                else if (_options.PostUnknownApps && messageId is null) messageId = await notifier.PostAsync(retiredApp, cancellationToken);

                status = isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata;
            }
        }

        var retirementMessageId = tracked.RetirementDiscordMessageId;

        if (RetirementTrackingEnabled && CanReportRetirement(tracked, app)){
            if (retirementMessageId is null){
                retirementMessageId = await retiredNotifier.PostAsync(retiredApp, cancellationToken);

                LogRetiredApp(logger, retiredApp.AppId, retirementMessageId.Value);
            }
            else{
                retirementMessageId = await retiredNotifier.UpdateAsync(retirementMessageId.Value, retiredApp, cancellationToken);

                LogUpdatedRetiredApp(logger, retiredApp.AppId, retirementMessageId.Value);
            }
        }

        var updated = tracked with{
            Name = retiredApp.Name,
            Kind = retiredApp.Kind,
            Status = status,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = messageId,
            UpdatedUtc = now,
            NextMetadataCheckUtc = status == TrackingStatus.PendingMetadata ? GetNextMetadataCheck(now) : null,
            RetirementDiscordMessageId = retirementMessageId,
        };

        await store.UpsertAppAsync(ApplyRetirementObservation(updated, app), cancellationToken);
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

        if (ShouldIgnoreRetirementObservation(tracked, app)) return;

        var messageId = tracked.DiscordMessageId;

        if (IsTerminalNonWantedKind(app.Kind)){
            var ignored = tracked with{
                Name = app.Name,
                Kind = app.Kind,
                Status = TrackingStatus.Ignored,
                LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
                DiscordMessageId = messageId,
                UpdatedUtc = now,
                NextMetadataCheckUtc = null,
            };

            await store.UpsertAppAsync(ApplyRetirementObservation(ignored, app), cancellationToken);

            return;
        }

        var isComplete = IsComplete(app);

        if (isComplete) messageId = messageId is null ? await notifier.PostAsync(app, cancellationToken) : await notifier.UpdateAsync(messageId.Value, app, cancellationToken);
        else if (_options.PostUnknownApps && messageId is null) messageId = await notifier.PostAsync(app, cancellationToken);

        var updated = tracked with{
            Name = app.Name,
            Kind = app.Kind,
            Status = isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = messageId,
            UpdatedUtc = now,
            NextMetadataCheckUtc = isComplete ? null : GetNextMetadataCheck(now),
        };

        await store.UpsertAppAsync(ApplyRetirementObservation(updated, app), cancellationToken);
    }

    private async Task BackfillRetirementStatesAsync(CancellationToken cancellationToken){
        var appIds = await store.GetPendingRetirementAppIdsAsync(_retirementBackfillCursor, RetirementBackfillBatchSize, cancellationToken);

        if (appIds.Count == 0){
            _retirementBackfillCursor = 0;

            return;
        }

        var metadata = await steam.GetPicsAppMetadataAsync(appIds, cancellationToken);

        var observations = new List<SteamAppMetadata>(appIds.Count);

        foreach (var appId in appIds){
            if (metadata.TryGetValue(appId, out var app) && app.AppId == appId) observations.Add(app);
        }

        if (observations.Count > 0) await store.InitializeRetirementStatesAsync(observations, cancellationToken);

        _retirementBackfillCursor = appIds[^1];
    }

    private async Task RecoverContinuityAsync(CancellationToken cancellationToken){
        if (_recoveryRequest is null){
            var previousCheckpoint = await store.GetLastChangeNumberAsync(cancellationToken) ?? throw new InvalidOperationException("Steam checkpoint is missing during recovery.");

            var recoveryChangeNumber = await steam.GetCurrentChangeNumberAsync(cancellationToken);

            if (recoveryChangeNumber < previousCheckpoint) throw new InvalidDataException("Steam returned a current change number older than the saved checkpoint. Recovery will retry without changing the checkpoint.");

            _recoveryRequest = await TryGetReplayableRecoveryRequestAsync(previousCheckpoint, recoveryChangeNumber, cancellationToken) ?? await baselineCoordinator.CreateRequestAsync(recoveryChangeNumber, cancellationToken);
        }

        var request = _recoveryRequest;

        LogRecoveryRequired(logger, request.StartChangeNumber, baselineCoordinator.RequestPath);

        var baseline = await baselineCoordinator.WaitForBaselineAsync(request, cancellationToken);

        var changes = await steam.GetChangesSinceAsync(request.StartChangeNumber, cancellationToken);

        await ValidateRecoveryChangesAsync(changes, request.StartChangeNumber, cancellationToken);

        var discoveredCount = 0;

        foreach (var batch in baseline.Apps.Chunk(RecoveryBatchSize)){
            cancellationToken.ThrowIfCancellationRequested();

            discoveredCount += await store.MergeRecoveryBaselineAsync(batch, request.StartChangeNumber, cancellationToken);
        }

        changes = await steam.GetChangesSinceAsync(request.StartChangeNumber, cancellationToken);
        await ValidateRecoveryChangesAsync(changes, request.StartChangeNumber, cancellationToken);

        await ProcessChangedAppsAsync(changes, cancellationToken);

        await store.SetLastChangeNumberAsync(changes.CurrentChangeNumber, cancellationToken);

        _recoveryRequest = null;

        LogRecoveryCompleted(logger, discoveredCount, changes.CurrentChangeNumber);
    }

    private async Task<SteamBaselineRequest?> TryGetReplayableRecoveryRequestAsync(uint savedCheckpoint, uint currentChangeNumber, CancellationToken cancellationToken){
        var request = await baselineCoordinator.TryGetRecoveryRequestAsync(savedCheckpoint, currentChangeNumber, cancellationToken);

        if (request is null) return null;

        var changes = await steam.GetChangesSinceAsync(request.StartChangeNumber, cancellationToken);

        return changes.RequiresFullAppUpdate ? null : request;
    }

    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private async Task ValidateRecoveryChangesAsync(SteamChangeSet changes, uint recoveryChangeNumber, CancellationToken cancellationToken){
        if (changes.CurrentChangeNumber < recoveryChangeNumber) throw new InvalidDataException("Steam returned a change number older than the recovery request. The saved checkpoint has not been advanced.");

        if (!changes.RequiresFullAppUpdate) return;

        _recoveryRequest = await baselineCoordinator.CreateRequestAsync(changes.CurrentChangeNumber, cancellationToken);

        throw new InvalidDataException("Steam cannot replay changes from the recovery baseline. A fresh baseline request has been created; the saved checkpoint has not been advanced.");
    }

    private static bool ShouldIgnoreRetirementObservation(TrackedSteamApp tracked, SteamAppMetadata app){
        if (tracked.RetirementObservedChange is not{ } observedChange) return false;

        return app.ChangeNumber < observedChange || (app.ChangeNumber == observedChange && app.IsRetired != tracked.IsRetired);
    }

    private static TrackedSteamApp ApplyRetirementObservation(TrackedSteamApp tracked, SteamAppMetadata? app){
        if (app is null || ShouldIgnoreRetirementObservation(tracked, app)) return tracked;

        return tracked with{
            IsRetired = app.IsRetired,
            RetirementObservedChange = app.ChangeNumber,
        };
    }

    private static bool CanReportRetirement(TrackedSteamApp tracked, SteamAppMetadata app) => app.IsRetired && tracked.RetirementObservedChange is{ } observedChange && app.ChangeNumber > observedChange && (!tracked.IsRetired || tracked.RetirementDiscordMessageId is not null);

    private static SteamAppKind GetRetirementKind(TrackedSteamApp tracked, SteamAppMetadata app) => app.Kind == SteamAppKind.Unknown ? tracked.Kind : app.Kind;

    private static bool IsTerminalNonWantedKind(SteamAppKind kind) => kind is SteamAppKind.Other or SteamAppKind.Application;

    private static bool IsComplete(SteamAppMetadata app) => app.Kind.IsWanted() && !string.IsNullOrWhiteSpace(app.Name);

    private DateTimeOffset GetNextMetadataCheck(DateTimeOffset now) => now.AddSeconds(_options.MetadataRetrySeconds);

    private static TrackedSteamApp CreateTracked(SteamAppMetadata app, TrackingStatus status, uint changeNumber, ulong? messageId, DateTimeOffset now, DateTimeOffset? nextCheck, ulong? retirementMessageId, uint? retirementObservedChange) => new(){
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
        RetirementObservedChange = retirementObservedChange,
        RetirementDiscordMessageId = retirementMessageId,
    };

    [LoggerMessage(2000, LogLevel.Error, "Steam app monitoring failed; retrying after the failure delay.")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception);
    [LoggerMessage(2001, LogLevel.Information, "Resuming from Steam change number {ChangeNumber}.")]
    private static partial void LogResuming(ILogger logger, uint changeNumber);
    [LoggerMessage(2002, LogLevel.Warning, "A Steam baseline is required using request file {RequestPath}.")]
    private static partial void LogBaselineRequired(ILogger logger, string requestPath);
    [LoggerMessage(2003, LogLevel.Information, "Seeded {AppCount} existing AppIDs at change number {ChangeNumber}; no historical notifications were sent.")]
    private static partial void LogSeeded(ILogger logger, int appCount, uint changeNumber);
    [LoggerMessage(2004, LogLevel.Information, "Found {Count} first-seen Steam AppID(s).")]
    private static partial void LogFirstSeenApps(ILogger logger, int count);
    [LoggerMessage(2005, LogLevel.Warning, "Steam requested a full app update from checkpoint {ChangeNumber}. The checkpoint is preserved; app monitoring will retry or request baseline recovery. The host remains running.")]
    private static partial void LogContinuityLost(ILogger logger, uint changeNumber);
    [LoggerMessage(2006, LogLevel.Information, "Posted retirement notification for Steam AppID {AppId} as message {MessageId}.")]
    private static partial void LogRetiredApp(ILogger logger, uint appId, ulong messageId);
    [LoggerMessage(2007, LogLevel.Information, "Updated retirement notification for Steam AppID {AppId} as message {MessageId}.")]
    private static partial void LogUpdatedRetiredApp(ILogger logger, uint appId, ulong messageId);
    [LoggerMessage(2008, LogLevel.Warning, "App monitoring requires a recovery baseline starting at change {ChangeNumber}, using request file {RequestPath}. Existing app rows and review subscriptions are preserved.")]
    private static partial void LogRecoveryRequired(ILogger logger, uint changeNumber, string requestPath);
    [LoggerMessage(2009, LogLevel.Information, "Steam app recovery completed: silently added {AppCount} previously untracked AppIDs and resumed at change {ChangeNumber}.")]
    private static partial void LogRecoveryCompleted(ILogger logger, int appCount, uint changeNumber);
}