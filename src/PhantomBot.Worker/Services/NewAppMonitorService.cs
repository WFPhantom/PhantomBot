using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Services;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class NewAppMonitorService(ISteamCatalogClient steam, ITrackedAppStore store, INewAppNotifier notifier, SteamBaselineCoordinator baselineCoordinator, IOptions<PhantomBotOptions> options, IHostApplicationLifetime applicationLifetime, ILogger<NewAppMonitorService> logger) : BackgroundService{
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

        var appsToRefresh = tracked.Values.Where(static app => app.Status is TrackingStatus.PendingMetadata or TrackingStatus.SeededIncomplete or TrackingStatus.Ignored or TrackingStatus.Announced).ToArray();

        if (appsToRefresh.Length > 0) await ResolvePendingAppsAsync(appsToRefresh, cancellationToken);
    }

    private async Task ProcessNewAppsAsync(IReadOnlyCollection<uint> appIds, IReadOnlyDictionary<uint, uint> changeNumbers, CancellationToken cancellationToken){
        var metadata = await steam.GetAppMetadataAsync(appIds, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // ReSharper disable once ConvertClosureToMethodGroup
        foreach (var appId in appIds.OrderBy(id => changeNumbers.GetValueOrDefault(id))){
            var changeNumber = changeNumbers.GetValueOrDefault(appId);

            var app = metadata.GetValueOrDefault(appId) ?? new SteamAppMetadata(appId, null, SteamAppKind.Unknown, changeNumber);

            if (app.Kind == SteamAppKind.Other){
                await store.UpsertAppAsync(CreateTracked(app, TrackingStatus.Ignored, changeNumber, null, now, null), cancellationToken);

                continue;
            }

            var isComplete = IsComplete(app);
            ulong? messageId = null;

            if (isComplete || _options.PostUnknownApps) messageId = await notifier.PostAsync(app, cancellationToken);

            var status = isComplete ? TrackingStatus.Announced : TrackingStatus.PendingMetadata;

            DateTimeOffset? nextCheck = isComplete ? null : GetNextMetadataCheck(now);

            await store.UpsertAppAsync(CreateTracked(app, status, changeNumber, messageId, now, nextCheck), cancellationToken);
        }
    }

    private async Task RetryPendingMetadataAsync(CancellationToken cancellationToken){
        var pending = await store.GetPendingMetadataAsync(DateTimeOffset.UtcNow, PendingMetadataBatchSize, cancellationToken);

        if (pending.Count > 0) await ResolvePendingAppsAsync(pending, cancellationToken);
    }

    private async Task ResolvePendingAppsAsync(IReadOnlyCollection<TrackedSteamApp> pending, CancellationToken cancellationToken){
        var metadata = await steam.GetAppMetadataAsync([.. pending.Select(static app => app.AppId)], cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var tracked in pending){
            metadata.TryGetValue(tracked.AppId, out var app);

            if (tracked.Status == TrackingStatus.Seeded) continue;

            var preserveHistoricalOrigin = tracked.Status == TrackingStatus.SeededIncomplete && (app is null || app.ChangeNumber <= tracked.FirstSeenChange);

            if (preserveHistoricalOrigin){
                await ResolveHistoricalAppAsync(tracked, app, now, cancellationToken);

                continue;
            }

            await ResolveNewAppAsync(tracked, app, now, cancellationToken);
        }
    }

    private async Task ResolveHistoricalAppAsync(TrackedSteamApp tracked, SteamAppMetadata? app, DateTimeOffset now, CancellationToken cancellationToken){
        if (tracked.Status == TrackingStatus.Seeded) return;

        if (app is null){
            await store.UpsertAppAsync(tracked with{
                Status = TrackingStatus.SeededIncomplete,
                DiscordMessageId = null,
                UpdatedUtc = now,
                NextMetadataCheckUtc = GetNextMetadataCheck(now),
            }, cancellationToken);
            return;
        }

        var isResolved = app.Kind == SteamAppKind.Other || IsComplete(app);

        await store.UpsertAppAsync(tracked with{
            Name = app.Name,
            Kind = app.Kind,
            Status = isResolved ? TrackingStatus.Seeded : TrackingStatus.SeededIncomplete,
            LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
            DiscordMessageId = null,
            UpdatedUtc = now,
            NextMetadataCheckUtc = isResolved ? null : GetNextMetadataCheck(now),
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

        if (app.Kind == SteamAppKind.Other){
            await store.UpsertAppAsync(tracked with{
                Name = app.Name,
                Kind = app.Kind,
                Status = TrackingStatus.Ignored,
                LastSeenChange = Math.Max(tracked.LastSeenChange, app.ChangeNumber),
                DiscordMessageId = messageId,
                UpdatedUtc = now,
                NextMetadataCheckUtc = null,
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
        }, cancellationToken);
    }

    private static bool IsComplete(SteamAppMetadata app) => app.Kind.IsWanted() && !string.IsNullOrWhiteSpace(app.Name);

    private DateTimeOffset GetNextMetadataCheck(DateTimeOffset now) => now.AddSeconds(_options.MetadataRetrySeconds);

    private static TrackedSteamApp CreateTracked(SteamAppMetadata app, TrackingStatus status, uint changeNumber, ulong? messageId, DateTimeOffset now, DateTimeOffset? nextCheck) => new(){
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
}