using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Services;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class ReviewMonitorService(ISteamReviewClient steam, IReviewSubscriptionStore store, IReviewNotifier notifier, IOptions<PhantomBotOptions> options, ILogger<ReviewMonitorService> logger, ITrackedAppStore trackedAppStore, ISteamCatalogClient catalog, TimeProvider? timeProvider = null) : BackgroundService{
    private const int MaximumInitializationPasses = 3;
    private const int PendingReviewBatchSize = 25;
    private const int SubscriptionBatchSize = 20;
    private const int ScanCleanupBatchSize = 20;
    private static readonly TimeSpan SteamRequestInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CycleFailureDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AppNameLookupTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _pollInterval = GetPollInterval(options.Value);
    private readonly Dictionary<long, ScanProgress> _scans = [];
    private DateTimeOffset _nextSteamRequestUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDeliveryAttemptUtc = DateTimeOffset.MinValue;
    private int _deliveryFailures;
    private (long PendingReviewId, ulong MessageId)? _unrecordedPost;
    private long _scanCleanupAfterId;
    private long _scanCleanupThroughId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken){
        try{
            LogStarted(logger);

            while (!stoppingToken.IsCancellationRequested){
                var delay = WorkerInterval;

                try{
                    await RunPollingCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested){
                    break;
                }
                catch (Exception exception){
                    LogCycleFailed(logger, exception);

                    delay = CycleFailureDelay;
                }

                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested){
            // Normal host shutdown.
        }
    }

    internal async Task RunPollingCycleAsync(CancellationToken cancellationToken){
        await DeliverPendingReviewsAsync(cancellationToken);
        await CleanupRemovedScansAsync(cancellationToken);

        var subscriptions = await store.GetDueSubscriptionsAsync(_timeProvider.GetUtcNow(), SubscriptionBatchSize, cancellationToken);

        foreach (var subscription in subscriptions){
            cancellationToken.ThrowIfCancellationRequested();

            try{
                await ProcessSubscriptionPageAsync(subscription, cancellationToken);

                if (_scans.ContainsKey(subscription.Id)){
                    var nextCheckUtc = _timeProvider.GetUtcNow() + WorkerInterval;

                    if (!await store.ScheduleNextPageAsync(subscription.Id, nextCheckUtc, cancellationToken)) _scans.Remove(subscription.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
                throw;
            }
            catch (Exception exception){
                _scans.Remove(subscription.Id);

                var nextCheckUtc = _timeProvider.GetUtcNow() + GetSubscriptionRetryDelay(subscription.ConsecutiveFailures);

                var failureReason = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

                if (await store.RecordFailureAsync(subscription.Id, failureReason, nextCheckUtc, cancellationToken)) LogSubscriptionFailed(logger, subscription.Id, subscription.Source.Name, nextCheckUtc, exception);
            }
            await DeliverPendingReviewsAsync(cancellationToken);
        }
    }

    private async Task ProcessSubscriptionPageAsync(SteamReviewSubscription subscription, CancellationToken cancellationToken){
        if (!_scans.TryGetValue(subscription.Id, out var scan)){
            await WaitForSteamRequestAsync(cancellationToken);

            var source = await steam.ResolveSourceAsync(subscription.Source.Url, cancellationToken);

            if (source.Kind != subscription.Source.Kind || source.Id != subscription.Source.Id) throw new InvalidDataException("Steam resolved the subscription to a different source.");

            scan = new ScanProgress(source);

            _scans.Add(subscription.Id, scan);

            if (subscription.Status == SteamReviewSubscriptionStatus.Initializing) LogInitializationStarted(logger, subscription.Id, source.Name);
        }

        var cursorKey = scan.Cursor ?? string.Empty;

        if (!scan.RequestedCursors.Add(cursorKey)) throw new InvalidDataException("Steam review pagination repeated a previously requested cursor.");

        await WaitForSteamRequestAsync(cancellationToken);

        var page = await steam.GetReviewPageAsync(scan.Source, scan.Cursor, cancellationToken);

        ValidatePage(page, scan);

        switch (subscription.Status){
            case SteamReviewSubscriptionStatus.Initializing:{
                var addedCount = await store.SeedReviewPageAsync(subscription.Id, [.. page.Reviews.Select(static review => review.AppId)], cancellationToken);

                if (addedCount is null){
                    _scans.Remove(subscription.Id);

                    return;
                }

                scan.AddedThisPass += addedCount.Value;
                break;
            }
            case SteamReviewSubscriptionStatus.Active:{
                if (!await store.EnqueueNewReviewsAsync(subscription.Id, page.Reviews, cancellationToken)){
                    _scans.Remove(subscription.Id);

                    return;
                }

                break;
            }
            default:
                throw new InvalidDataException("The review subscription has an unknown status.");
        }

        scan.Cursor = page.NextCursor;

        if (scan.Cursor is not null) return;

        if (subscription.Status == SteamReviewSubscriptionStatus.Active){
            var completedUtc = _timeProvider.GetUtcNow();

            await store.CompletePollAsync(subscription.Id, scan.Source, completedUtc, completedUtc + _pollInterval, cancellationToken);

            _scans.Remove(subscription.Id);

            return;
        }

        scan.CompletedPasses++;

        LogInitializationPassCompleted(logger, subscription.Id, scan.Source.Name, scan.CompletedPasses, scan.AddedThisPass);

        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (scan is{ CompletedPasses: >= 2, AddedThisPass: 0 }){
            var completedUtc = _timeProvider.GetUtcNow();

            if (await store.CompleteInitializationAsync(subscription.Id, scan.Source, completedUtc, completedUtc + _pollInterval, cancellationToken)) LogSubscriptionActivated(logger, subscription.Id, scan.Source.Name);

            _scans.Remove(subscription.Id);

            return;
        }

        if (scan.CompletedPasses >= MaximumInitializationPasses) throw new InvalidDataException("The review history did not stabilize within three complete passes. The subscription remains initializing.");

        scan.AddedThisPass = 0;
        scan.RequestedCursors.Clear();
    }

    private async Task DeliverPendingReviewsAsync(CancellationToken cancellationToken){
        if (_timeProvider.GetUtcNow() < _nextDeliveryAttemptUtc) return;

        try{
            await PersistDeliveryReceiptAsync(cancellationToken);

            var pendingReviews = await store.GetPendingReviewsAsync(PendingReviewBatchSize, cancellationToken);

            if (pendingReviews.Count > 0){
                var appNames = await ResolveMissingAppNamesAsync(pendingReviews, cancellationToken);
                var subscriptionIds = pendingReviews.Select(static review => review.SubscriptionId).Distinct().ToArray();
                var subscriptions = await store.GetSubscriptionsByIdsAsync(subscriptionIds, cancellationToken);

                var activeIds = subscriptions.Where(static subscription => subscription.Status == SteamReviewSubscriptionStatus.Active).Select(static subscription => subscription.Id).ToHashSet();

                foreach (var pendingReview in pendingReviews){
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!activeIds.Contains(pendingReview.SubscriptionId)) continue;

                    var delivery = pendingReview;

                    if (string.IsNullOrWhiteSpace(pendingReview.Review.AppName) && appNames.TryGetValue(pendingReview.Review.AppId, out var appName)){
                        delivery = pendingReview with{
                            Review = pendingReview.Review with{
                                AppName = appName,
                            },
                        };
                    }

                    var messageId = await notifier.PostAsync(delivery, cancellationToken);

                    if (messageId == 0) throw new InvalidDataException("The review notifier returned an invalid Discord message ID.");

                    _unrecordedPost = (pendingReview.Id, messageId);

                    await PersistDeliveryReceiptAsync(cancellationToken);
                }
            }

            _deliveryFailures = 0;
            _nextDeliveryAttemptUtc = DateTimeOffset.MinValue;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
            throw;
        }
        catch (Exception exception){
            var retryDelay = GetDeliveryRetryDelay(_deliveryFailures);

            _deliveryFailures = Math.Min(_deliveryFailures + 1, 4);
            _nextDeliveryAttemptUtc = _timeProvider.GetUtcNow() + retryDelay;

            LogDeliveryFailed(logger, _nextDeliveryAttemptUtc, exception);
        }
    }

    private async Task<Dictionary<uint, string>> ResolveMissingAppNamesAsync(IEnumerable<PendingSteamReview> pendingReviews, CancellationToken cancellationToken){
        var appIds = pendingReviews.Where(static pending => string.IsNullOrWhiteSpace(pending.Review.AppName)).Select(static pending => pending.Review.AppId).Distinct().ToArray();

        var names = new Dictionary<uint, string>();

        if (appIds.Length == 0) return names;

        try{
            var trackedApps = await trackedAppStore.GetAppsAsync(appIds, cancellationToken);

            foreach (var appId in appIds){
                if (trackedApps.TryGetValue(appId, out var app) && !string.IsNullOrWhiteSpace(app.Name)) names.Add(appId, app.Name.Trim());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
            throw;
        }
        catch (Exception exception){
            LogTrackedAppNameLookupFailed(logger, exception);
        }

        var missingIds = appIds.Where(appId => !names.ContainsKey(appId)).ToArray();

        if (missingIds.Length == 0) return names;

        try{
            using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lookupCancellation.CancelAfter(AppNameLookupTimeout);

            var metadata = await catalog.GetPicsAppMetadataAsync(missingIds, lookupCancellation.Token);

            foreach (var appId in missingIds){
                if (metadata.TryGetValue(appId, out var app) && app.AppId == appId && !string.IsNullOrWhiteSpace(app.Name)) names.Add(appId, app.Name.Trim());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested){
            throw;
        }
        catch (Exception exception){
            LogPicsAppNameLookupFailed(logger, exception);
        }

        return names;
    }

    private async Task PersistDeliveryReceiptAsync(CancellationToken cancellationToken){
        if (_unrecordedPost is not{ } receipt) return;

        var marked = await store.MarkReviewPostedAsync(receipt.PendingReviewId, receipt.MessageId, cancellationToken);

        _unrecordedPost = null;

        if (marked) LogReviewPosted(logger, receipt.PendingReviewId, receipt.MessageId);
    }

    private async Task WaitForSteamRequestAsync(CancellationToken cancellationToken){
        var delay = _nextSteamRequestUtc - _timeProvider.GetUtcNow();

        if (delay > TimeSpan.Zero) await Task.Delay(delay, _timeProvider, cancellationToken);

        _nextSteamRequestUtc = _timeProvider.GetUtcNow() + SteamRequestInterval;
    }

    private static void ValidatePage(SteamReviewPage page, ScanProgress scan){
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Reviews);

        if (page.NextCursor is not null){
            if (string.IsNullOrWhiteSpace(page.NextCursor) || scan.RequestedCursors.Contains(page.NextCursor)) throw new InvalidDataException("Steam returned invalid or repeating review pagination.");

            if (page.Reviews.Count == 0) throw new InvalidDataException("Steam returned an empty review page with more pages remaining.");
        }

        if (page.Reviews.Any(review => review.AppId == 0 || review.Source.Kind != scan.Source.Kind || review.Source.Id != scan.Source.Id)) throw new InvalidDataException("Steam returned a review with an invalid app or mismatched source.");
    }

    private static TimeSpan GetPollInterval(PhantomBotOptions options){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReviewPollIntervalSeconds);

        return TimeSpan.FromSeconds(options.ReviewPollIntervalSeconds);
    }

    private static TimeSpan GetSubscriptionRetryDelay(int previousFailures){
        var multiplier = 1 << Math.Clamp(previousFailures, 0, 3);

        return TimeSpan.FromMinutes(30 * multiplier);
    }

    private static TimeSpan GetDeliveryRetryDelay(int previousFailures){
        var seconds = Math.Min(30 * (1 << Math.Clamp(previousFailures, 0, 4)), 300);

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task CleanupRemovedScansAsync(CancellationToken cancellationToken){
        if (_scans.Count == 0){
            _scanCleanupAfterId = 0;
            _scanCleanupThroughId = 0;

            return;
        }

        if (_scanCleanupThroughId == 0){
            _scanCleanupAfterId = 0;
            _scanCleanupThroughId = _scans.Keys.Max();
        }

        var subscriptionIds = _scans.Keys.Where(id => id > _scanCleanupAfterId && id <= _scanCleanupThroughId).Order().Take(ScanCleanupBatchSize).ToArray();

        if (subscriptionIds.Length == 0){
            _scanCleanupAfterId = 0;
            _scanCleanupThroughId = 0;

            return;
        }

        var subscriptions = await store.GetSubscriptionsByIdsAsync(subscriptionIds, cancellationToken);

        var existingIds = subscriptions.Select(static subscription => subscription.Id).ToHashSet();

        foreach (var subscriptionId in subscriptionIds.Where(subscriptionId => !existingIds.Contains(subscriptionId))) _scans.Remove(subscriptionId);

        _scanCleanupAfterId = subscriptionIds[^1];

        if (subscriptionIds.Length < ScanCleanupBatchSize || _scanCleanupAfterId >= _scanCleanupThroughId){
            _scanCleanupAfterId = 0;
            _scanCleanupThroughId = 0;
        }
    }

    private sealed class ScanProgress(SteamReviewSource source){
        public SteamReviewSource Source{ get; } = source;
        public string? Cursor{ get; set; }
        public int CompletedPasses{ get; set; }
        public long AddedThisPass{ get; set; }
        public HashSet<string> RequestedCursors{ get; } = new(StringComparer.Ordinal);
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Steam review monitoring started.")]
    private static partial void LogStarted(ILogger logger);
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Initializing review subscription {SubscriptionId} for {SourceName}. Existing reviews will be seeded without notifications.")]
    private static partial void LogInitializationStarted(ILogger logger, long subscriptionId, string sourceName);
    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Review subscription {SubscriptionId} for {SourceName} completed initialization pass {PassNumber}; added {AddedCount} previously unseen AppIDs.")]
    private static partial void LogInitializationPassCompleted(ILogger logger, long subscriptionId, string sourceName, int passNumber, long addedCount);
    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Review subscription {SubscriptionId} for {SourceName} is active.")]
    private static partial void LogSubscriptionActivated(ILogger logger, long subscriptionId, string sourceName);
    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Review subscription {SubscriptionId} for {SourceName} failed. Next attempt: {NextCheckUtc}.")]
    private static partial void LogSubscriptionFailed(ILogger logger, long subscriptionId, string sourceName, DateTimeOffset nextCheckUtc, Exception exception);
    [LoggerMessage(EventId = 6005, Level = LogLevel.Information, Message = "Posted pending review {PendingReviewId} as Discord message {MessageId}.")]
    private static partial void LogReviewPosted(ILogger logger, long pendingReviewId, ulong messageId);
    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Review delivery failed. Next attempt: {NextAttemptUtc}.")]
    private static partial void LogDeliveryFailed(ILogger logger, DateTimeOffset nextAttemptUtc, Exception exception);
    [LoggerMessage(EventId = 6007, Level = LogLevel.Error, Message = "The review monitoring cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
    [LoggerMessage(EventId = 6008, Level = LogLevel.Warning, Message = "Tracked-app name lookup for pending reviews failed; trying PICS for missing names.")]
    private static partial void LogTrackedAppNameLookupFailed(ILogger logger, Exception exception);
    [LoggerMessage(EventId = 6009, Level = LogLevel.Warning, Message = "PICS app-name lookup for pending reviews failed; continuing with available names or AppID placeholders.")]
    private static partial void LogPicsAppNameLookupFailed(ILogger logger, Exception exception);
}