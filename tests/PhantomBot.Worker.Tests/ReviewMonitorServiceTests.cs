using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;
using PhantomBot.Infrastructure.Persistence;
using PhantomBot.Worker.Services;

namespace PhantomBot.Worker.Tests;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
// ReSharper disable once MemberCanBeFileLocal
public sealed class ReviewMonitorServiceTests{
    [Fact]
    public async Task InitializationRequiresCompleteVerificationPassAndPostsNoHistory(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);

        test.Steam.QueuePage(source, null, "2", 570);
        test.Steam.QueuePage(source, "2", null, 730);
        test.Steam.QueuePage(source, null, "2", 570);
        test.Steam.QueuePage(source, "2", null, 730);

        for (var cycle = 0; cycle < 3; cycle++){
            if (cycle == 0) await test.RunCycleAsync();
            else await test.RunNextCycleAsync();

            var current = await test.ReadAsync(subscription.Id);

            Assert.Equal(SteamReviewSubscriptionStatus.Initializing, current.Status);
            Assert.Empty(test.Notifier.Posted);
            Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
        }

        await test.RunNextCycleAsync();

        var active = await test.ReadAsync(subscription.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Active, active.Status);
        Assert.Equal(test.Clock.GetUtcNow(), active.LastSuccessfulCheckUtc);
        Assert.Equal(test.Clock.GetUtcNow().AddMinutes(5), active.NextCheckUtc);
        Assert.Equal(0, active.ConsecutiveFailures);
        Assert.Equal(4, test.Steam.Requests.Count);
        Assert.Single(test.Steam.ResolvedSourceIds);
        Assert.Empty(test.Notifier.Posted);
    }

    [Fact]
    public async Task ActivePollPostsOnlyNewReviewsAndDoesNotRepostEdits(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddActiveAsync(source, test.Clock.GetUtcNow(), 570);

        test.Steam.QueuePage(source, null, null, 570, 730);

        await test.RunCycleAsync();

        var posted = Assert.Single(test.Notifier.Posted);

        Assert.Equal(subscription.Id, posted.SubscriptionId);
        Assert.Equal(730U, posted.Review.AppId);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));

        var current = await test.ReadAsync(subscription.Id);

        test.Steam.QueueResponse(source, null, () => Task.FromResult(new SteamReviewPage(
        [
            CreateReview(source, 570),
            CreateReview(source, 730) with{
                Text = "An edited version of the same review.",
            },
        ], null)));

        test.Clock.SetUtcNow(current.NextCheckUtc);

        await test.RunCycleAsync();

        Assert.Single(test.Notifier.Posted);
        Assert.Equal(1, test.Notifier.PostAttempts);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task UnstableHistoryStaysInitializingAndRetriesWithoutPostingHistory(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);

        test.Steam.QueuePage(source, null, null, 570);
        test.Steam.QueuePage(source, null, null, 570, 730);
        test.Steam.QueuePage(source, null, null, 570, 730, 440);

        await test.RunCycleAsync();
        await test.RunNextCycleAsync();
        await test.RunNextCycleAsync();

        var failed = await test.ReadAsync(subscription.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, failed.Status);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(test.Clock.GetUtcNow().AddMinutes(30), failed.NextCheckUtc);
        Assert.Null(failed.LastSuccessfulCheckUtc);

        var failureReason = failed.LastError;
        Assert.NotNull(failureReason);
        Assert.Contains("did not stabilize", failureReason, StringComparison.Ordinal);
        Assert.Empty(test.Notifier.Posted);

        test.Clock.SetUtcNow(failed.NextCheckUtc.AddTicks(-1));

        await test.RunCycleAsync();

        Assert.Equal(3, test.Steam.Requests.Count);

        test.Steam.QueuePage(source, null, null, 570, 730, 440);
        test.Steam.QueuePage(source, null, null, 570, 730, 440);
        test.Clock.SetUtcNow(failed.NextCheckUtc);

        await test.RunCycleAsync();

        var retrying = await test.ReadAsync(subscription.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, retrying.Status);
        Assert.Equal(1, retrying.ConsecutiveFailures);
        Assert.Equal(failed.LastError, retrying.LastError);

        await test.RunNextCycleAsync();

        var active = await test.ReadAsync(subscription.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Active, active.Status);
        Assert.Equal(0, active.ConsecutiveFailures);
        Assert.Null(active.LastError);
        Assert.Empty(test.Notifier.Posted);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task SubscriptionFailuresUseIncreasingRetryDelayWithFourHourCap(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);
        int[] retryMinutes = [30, 60, 120, 240, 240];

        for (var attempt = 0; attempt < retryMinutes.Length; attempt++){
            test.Steam.QueueResponse(source, null, static () => Task.FromException<SteamReviewPage>(new HttpRequestException("Steam is unavailable.")));

            test.Clock.SetUtcNow(subscription.NextCheckUtc);

            await test.RunCycleAsync();

            subscription = await test.ReadAsync(subscription.Id);

            Assert.Equal(SteamReviewSubscriptionStatus.Initializing, subscription.Status);
            Assert.Equal(attempt + 1, subscription.ConsecutiveFailures);
            Assert.Equal(test.Clock.GetUtcNow().AddMinutes(retryMinutes[attempt]), subscription.NextCheckUtc);
        }

        Assert.Empty(test.Notifier.Posted);
    }

    [Fact]
    public async Task RestartDuringInitializationRestartsPaginationAndPreservesSeededHistory(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);

        test.Steam.QueuePage(source, null, "2", 570);

        await test.RunCycleAsync();

        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, (await test.ReadAsync(subscription.Id)).Status);

        test.RestartMonitor();
        test.Steam.QueuePage(source, null, null, 570, 730);
        test.Steam.QueuePage(source, null, null, 570, 730);

        await test.RunNextCycleAsync();
        await test.RunNextCycleAsync();

        Assert.Equal(SteamReviewSubscriptionStatus.Active, (await test.ReadAsync(subscription.Id)).Status);
        Assert.Equal(3, test.Steam.Requests.Count);
        Assert.All(test.Steam.Requests, static request => Assert.Null(request.Cursor));
        Assert.Equal(2, test.Steam.ResolvedSourceIds.Count);
        Assert.Empty(test.Notifier.Posted);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task FailedDiscordSendRemainsQueuedUntilRetryIsDue(){
        using var test = await TestState.CreateAsync();

        var pending = await test.QueueDeliveryAsync();

        test.Notifier.FailuresRemaining = 1;

        await test.RunCycleAsync();

        Assert.Equal(1, test.Notifier.PostAttempts);
        Assert.Empty(test.Notifier.Posted);

        Assert.Equal(pending, Assert.Single(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None)));

        test.Clock.Advance(TimeSpan.FromSeconds(29));

        await test.RunCycleAsync();

        Assert.Equal(1, test.Notifier.PostAttempts);

        test.Clock.Advance(TimeSpan.FromSeconds(1));

        await test.RunCycleAsync();

        Assert.Equal(2, test.Notifier.PostAttempts);
        Assert.Equal(pending, Assert.Single(test.Notifier.Posted));
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulSendWithFailedReceiptWriteRetriesPersistenceWithoutReposting(){
        using var test = await TestState.CreateAsync();

        var first = await test.QueueDeliveryAsync();

        Assert.True(await test.Store.EnqueueNewReviewsAsync(first.SubscriptionId, [CreateReview(first.Review.Source, 730)], CancellationToken.None));

        var queued = await test.Store.GetPendingReviewsAsync(10, CancellationToken.None);

        Assert.Equal(2, queued.Count);

        var second = queued[1];

        test.Store.ReceiptFailuresRemaining = 1;

        await test.RunCycleAsync();

        Assert.Equal(first, Assert.Single(test.Notifier.Posted));
        Assert.Equal(1, test.Notifier.PostAttempts);
        Assert.Equal(1, test.Store.ReceiptWriteAttempts);
        Assert.Equal(2, (await test.Store.GetPendingReviewsAsync(10, CancellationToken.None)).Count);

        test.Clock.Advance(TimeSpan.FromSeconds(29));

        await test.RunCycleAsync();

        Assert.Equal(1, test.Notifier.PostAttempts);
        Assert.Equal(1, test.Store.ReceiptWriteAttempts);

        test.Clock.Advance(TimeSpan.FromSeconds(1));

        await test.RunCycleAsync();

        Assert.Equal(2, test.Notifier.PostAttempts);
        Assert.Equal(3, test.Store.ReceiptWriteAttempts);
        Assert.Equal(2, test.Notifier.Posted.Count);
        Assert.Equal(first, test.Notifier.Posted[0]);
        Assert.Equal(second, test.Notifier.Posted[1]);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalDuringFetchCannotSeedOrQueueReviewsForReplacement(bool active){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var store = test.Store;
        var clock = test.Clock;

        var original = active ? await test.AddActiveAsync(source, test.Clock.GetUtcNow()) : await test.AddAsync(source);

        SteamReviewSubscription? replacement = null;

        test.Steam.QueueResponse(source, null, async () => {
            Assert.True(await store.RemoveAsync(original.Id, CancellationToken.None));
            Assert.True(await store.TryAddAsync(source, clock.GetUtcNow(), CancellationToken.None));

            var subscriptions = await store.GetSubscriptionsAsync(CancellationToken.None);

            replacement = Assert.Single(subscriptions, subscription => subscription.Source.Kind == source.Kind && subscription.Source.Id == source.Id);

            return new SteamReviewPage([CreateReview(source, 570)], null);
        });
        await test.RunCycleAsync();

        Assert.NotNull(replacement);
        Assert.True(replacement.Id > original.Id);

        var current = await test.ReadAsync(replacement.Id);

        Assert.Equal(replacement, current);
        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, current.Status);
        Assert.Equal(0, current.ConsecutiveFailures);
        Assert.Equal(1, await test.Store.SeedReviewPageAsync(replacement.Id, [570], CancellationToken.None));
        Assert.Empty(test.Notifier.Posted);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task LargeInitializationDoesNotBlockAnotherSourcesReviewDelivery(){
        using var test = await TestState.CreateAsync();

        var initializingSource = CreateSource();
        var activeSource = CreateSource(76_561_198_297_114_543UL);
        var initializing = await test.AddAsync(initializingSource);
        var active = await test.AddActiveAsync(activeSource, test.Clock.GetUtcNow());

        test.Steam.QueuePage(initializingSource, null, "2", 570);
        test.Steam.QueuePage(activeSource, null, null, 730);

        await test.RunCycleAsync();

        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, (await test.ReadAsync(initializing.Id)).Status);

        var posted = Assert.Single(test.Notifier.Posted);

        Assert.Equal(active.Id, posted.SubscriptionId);
        Assert.Equal(activeSource, posted.Review.Source);
        Assert.Equal(730U, posted.Review.AppId);
        Assert.Equal(2, test.Steam.Requests.Count);
    }

    [Fact]
    public async Task FortyDueSubscriptionsRotateInBatchesAndPreservePagination(){
        using var test = await TestState.CreateAsync();

        var sources = new List<SteamReviewSource>();

        for (var index = 0; index < 40; index++){
            var source = CreateSource(76_561_198_297_114_542UL + (ulong)index);

            sources.Add(source);

            await test.AddAsync(source);

            test.Steam.QueuePage(source, null, "2", 570);
            test.Steam.QueuePage(source, "2", "3", 730);
        }

        test.Store.ResetReadTracking();

        await test.RunCycleAsync();

        Assert.Equal(sources.Take(20).Select(static source => source.Id).ToArray(), test.Steam.Requests.Select(static request => request.SourceId).ToArray());

        await test.RunNextCycleAsync();

        Assert.Equal(sources.Select(static source => source.Id).ToArray(), test.Steam.Requests.Select(static request => request.SourceId).ToArray());

        await test.RunNextCycleAsync();
        await test.RunNextCycleAsync();

        Assert.Equal(80, test.Steam.Requests.Count);
        Assert.Equal(40, test.Steam.ResolvedSourceIds.Count);
        Assert.Equal(4, test.Store.DueReadLimits.Count);
        Assert.All(test.Store.DueReadLimits, static limit => Assert.Equal(20, limit));
        Assert.Equal(0, test.Store.AllSubscriptionsReadCount);

        Assert.NotEmpty(test.Store.IdReadRequests);

        Assert.All(test.Store.IdReadRequests, static ids => Assert.InRange(ids.Length, 1, 20));

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var source in sources){
            var requests = test.Steam.Requests.Where(request => request.SourceId == source.Id).ToArray();

            Assert.Equal(2, requests.Length);
            Assert.Null(requests[0].Cursor);
            Assert.Equal("2", requests[1].Cursor);
        }

        var subscriptions = await test.Store.GetSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(40, subscriptions.Count);

        foreach (var subscription in subscriptions){
            Assert.Equal(SteamReviewSubscriptionStatus.Initializing, subscription.Status);
            Assert.Equal(0, subscription.ConsecutiveFailures);
            Assert.Null(subscription.LastError);
        }

        Assert.Empty(test.Notifier.Posted);
    }

    [Fact]
    public async Task SubscriptionBecomingDueIsProcessedWhileAnotherIsInitializing(){
        using var test = await TestState.CreateAsync();

        var initializingSource = CreateSource();
        var activeSource = CreateSource(76_561_198_297_114_543UL);
        var becomesDueUtc = test.Clock.GetUtcNow().AddSeconds(10);
        var initializing = await test.AddAsync(initializingSource);
        var active = await test.AddActiveAsync(activeSource, becomesDueUtc);

        test.Steam.QueuePage(initializingSource, null, "2", 570);
        test.Steam.QueuePage(initializingSource, "2", "3", 730);
        test.Steam.QueuePage(activeSource, null, null, 440);

        await test.RunCycleAsync();

        Assert.Equal(initializingSource.Id, Assert.Single(test.Steam.Requests).SourceId);
        Assert.Empty(test.Notifier.Posted);

        test.Clock.SetUtcNow(becomesDueUtc);

        await test.RunCycleAsync();

        Assert.Equal(3, test.Steam.Requests.Count);

        var initializationRequests = test.Steam.Requests.Where(request => request.SourceId == initializingSource.Id).ToArray();

        Assert.Equal(2, initializationRequests.Length);
        Assert.Null(initializationRequests[0].Cursor);
        Assert.Equal("2", initializationRequests[1].Cursor);

        var posted = Assert.Single(test.Notifier.Posted);

        Assert.Equal(active.Id, posted.SubscriptionId);
        Assert.Equal(440U, posted.Review.AppId);

        var current = await test.ReadAsync(initializing.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, current.Status);
        Assert.Equal(0, current.ConsecutiveFailures);
    }

    [Fact]
    public async Task CleanupPreservesPaginationWhileNextPageIsNotYetDue(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);

        test.Steam.QueuePage(source, null, "2", 570);
        test.Steam.QueuePage(source, "2", null, 730);
        test.Steam.QueuePage(source, null, "2", 570);
        test.Steam.QueuePage(source, "2", null, 730);

        await test.RunCycleAsync();

        var scheduled = await test.ReadAsync(subscription.Id);

        Assert.Equal(test.Clock.GetUtcNow().AddSeconds(1), scheduled.NextCheckUtc);

        test.Store.ResetReadTracking();

        await test.RunCycleAsync();

        Assert.Single(test.Steam.Requests);
        Assert.Equal(new[]{ subscription.Id }, Assert.Single(test.Store.IdReadRequests));
        Assert.Equal(scheduled, await test.ReadAsync(subscription.Id));

        await test.RunNextCycleAsync();

        Assert.Equal(2, test.Steam.Requests.Count);
        Assert.Equal("2", test.Steam.Requests[1].Cursor);
        Assert.Single(test.Steam.ResolvedSourceIds);

        await test.RunNextCycleAsync();
        await test.RunNextCycleAsync();

        var active = await test.ReadAsync(subscription.Id);

        Assert.Equal(SteamReviewSubscriptionStatus.Active, active.Status);
        Assert.Equal(0, active.ConsecutiveFailures);
        Assert.Equal(4, test.Steam.Requests.Count);
        Assert.Single(test.Steam.ResolvedSourceIds);
        Assert.Empty(test.Notifier.Posted);
    }

    [Fact]
    public async Task CleanupRemovesDeletedScanAndStopsQueryingWhenNoScansRemain(){
        using var test = await TestState.CreateAsync();

        var source = CreateSource();
        var subscription = await test.AddAsync(source);

        test.Steam.QueuePage(source, null, "2", 570);

        await test.RunCycleAsync();

        Assert.True(await test.Store.RemoveAsync(subscription.Id, CancellationToken.None));

        test.Store.ResetReadTracking();

        await test.RunCycleAsync();

        Assert.Equal(new[]{ subscription.Id }, Assert.Single(test.Store.IdReadRequests));
        Assert.Single(test.Steam.Requests);

        test.Store.ResetReadTracking();

        await test.RunNextCycleAsync();

        Assert.Empty(test.Store.IdReadRequests);
        Assert.Equal(0, test.Store.AllSubscriptionsReadCount);
        Assert.Single(test.Steam.Requests);
        Assert.Empty(test.Notifier.Posted);
    }

    [Fact]
    public async Task DeliveryLooksUpOnlySubscriptionsReferencedByPendingReviews(){
        using var test = await TestState.CreateAsync();

        var pending = await test.QueueDeliveryAsync();

        for (var index = 1; index <= 3; index++) await test.AddActiveAsync(CreateSource(76_561_198_297_114_542UL + (ulong)index), test.Clock.GetUtcNow().AddHours(1));

        test.Store.ResetReadTracking();

        await test.RunCycleAsync();

        Assert.Equal(pending, Assert.Single(test.Notifier.Posted));
        Assert.Equal(0, test.Store.AllSubscriptionsReadCount);
        Assert.Equal(new[]{ pending.SubscriptionId }, Assert.Single(test.Store.IdReadRequests));
        Assert.Empty(test.Steam.Requests);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task MissingReviewNameUsesTrackedAppNameWithoutPicsRequest(){
        using var test = await TestState.CreateAsync();

        await test.TrackedApps.SeedAppsAsync([new SteamAppListEntry(570, "Dota 2", "game")], 100, CancellationToken.None);

        var pending = await test.QueueDeliveryAsync(null);

        await test.RunCycleAsync();

        var posted = Assert.Single(test.Notifier.Posted);

        Assert.Equal(pending with{
            Review = pending.Review with{
                AppName = "Dota 2",
            },
        }, posted);

        Assert.Empty(test.Catalog.PicsRequests);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task MissingReviewNamesUseOnePicsBatchWithoutAddingTrackedApps(){
        using var test = await TestState.CreateAsync();

        var first = await test.QueueDeliveryAsync(null);

        Assert.True(await test.Store.EnqueueNewReviewsAsync(first.SubscriptionId, [
            CreateReview(first.Review.Source, 730) with{
                AppName = null,
            },
        ], CancellationToken.None));

        test.Catalog.Metadata.Add(570, new SteamAppMetadata(570, "Dota 2", SteamAppKind.Game, 100));
        test.Catalog.Metadata.Add(730, new SteamAppMetadata(730, "Counter-Strike 2", SteamAppKind.Game, 100));

        await test.RunCycleAsync();

        var requestedIds = Assert.Single(test.Catalog.PicsRequests);

        Assert.Equal(new uint[]{ 570, 730 }, requestedIds.Order().ToArray());
        Assert.Equal(2, test.Notifier.Posted.Count);

        var firstPosted = Assert.Single(test.Notifier.Posted, static pending => pending.Review.AppId == 570);
        var secondPosted = Assert.Single(test.Notifier.Posted, static pending => pending.Review.AppId == 730);

        Assert.Equal("Dota 2", firstPosted.Review.AppName);
        Assert.Equal("Counter-Strike 2", secondPosted.Review.AppName);

        Assert.Empty(await test.TrackedApps.GetAppsAsync([570, 730], CancellationToken.None));
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task ExistingReviewNameIsPreserved(){
        using var test = await TestState.CreateAsync();

        await test.TrackedApps.SeedAppsAsync([new SteamAppListEntry(570, "Stored name", "game")], 100, CancellationToken.None);

        var pending = await test.QueueDeliveryAsync("Name from Steam review");

        await test.RunCycleAsync();

        Assert.Equal(pending, Assert.Single(test.Notifier.Posted));
        Assert.Empty(test.Catalog.PicsRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReviewNameStillPostsWhenPicsCannotProvideName(bool requestFails){
        using var test = await TestState.CreateAsync();

        var pending = await test.QueueDeliveryAsync(null);

        if (requestFails) test.Catalog.PicsFailure = new HttpRequestException("Steam is unavailable.");

        await test.RunCycleAsync();

        Assert.Equal(pending, Assert.Single(test.Notifier.Posted));
        Assert.Single(test.Catalog.PicsRequests);
        Assert.Equal(1, test.Notifier.PostAttempts);
        Assert.Empty(await test.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    private static SteamReviewSource CreateSource(ulong sourceId = 76_561_198_297_114_542UL) => new(){
        Kind = SteamReviewSourceKind.User,
        Id = sourceId,
        Name = "Test Reviewer",
        Url = $"https://steamcommunity.com/profiles/{sourceId.ToString(CultureInfo.InvariantCulture)}/",
    };

    private static SteamReview CreateReview(SteamReviewSource source, uint appId) => new(){
        Source = source,
        AppId = appId,
        AppName = "Test Game",
        Recommendation = SteamReviewRecommendation.Recommended,
        Text = "A test review.",
        Url = $"{source.Url}recommended/{appId.ToString(CultureInfo.InvariantCulture)}/",
    };

    private sealed class TestState : IDisposable{
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhantomBotReviewWorkerTests-{Guid.NewGuid():N}");
        public TestClock Clock{ get; } = new();
        public FakeSteam Steam{ get; }
        public FakeNotifier Notifier{ get; } = new();
        public ReceiptFailureStore Store{ get; }
        private ReviewMonitorService Monitor{ get; set; }
        public SqliteTrackedAppStore TrackedApps{ get; }
        public FakeSteamCatalogClient Catalog{ get; } = new();
        private TestState(){
            Directory.CreateDirectory(_directory);

            var options = Options.Create(new PhantomBotOptions{
                DatabasePath = Path.Combine(_directory, "phantombot.db"),
            });

            TrackedApps = new SqliteTrackedAppStore(options, new TestHostEnvironment(_directory));
            Store = new ReceiptFailureStore(new SqliteReviewSubscriptionStore(options, new TestHostEnvironment(_directory)));
            Steam = new FakeSteam(Clock);

            Monitor = CreateMonitor();
        }

        public static async Task<TestState> CreateAsync(){
            var test = new TestState();

            try{
                await test.Store.InitializeAsync(CancellationToken.None);
                await test.TrackedApps.InitializeAsync(CancellationToken.None);

                return test;
            }
            catch{
                test.Dispose();

                throw;
            }
        }

        public Task RunCycleAsync() => Monitor.RunPollingCycleAsync(CancellationToken.None);

        public Task RunNextCycleAsync(){
            Clock.Advance(TimeSpan.FromSeconds(1));

            return RunCycleAsync();
        }

        public void RestartMonitor(){
            Monitor.Dispose();

            Monitor = CreateMonitor();
        }

        public async Task<SteamReviewSubscription> AddAsync(SteamReviewSource source){
            Assert.True(await Store.TryAddAsync(source, Clock.GetUtcNow(), CancellationToken.None));

            var subscriptions = await Store.GetSubscriptionsAsync(CancellationToken.None);

            return Assert.Single(subscriptions, item => item.Source.Kind == source.Kind && item.Source.Id == source.Id);
        }

        public async Task<SteamReviewSubscription> AddActiveAsync(SteamReviewSource source, DateTimeOffset nextCheckUtc, params uint[] existingAppIds){
            var subscription = await AddAsync(source);

            Assert.NotNull(await Store.SeedReviewPageAsync(subscription.Id, existingAppIds, CancellationToken.None));
            Assert.True(await Store.CompleteInitializationAsync(subscription.Id, source, Clock.GetUtcNow(), nextCheckUtc, CancellationToken.None));

            return await ReadAsync(subscription.Id);
        }

        public async Task<SteamReviewSubscription> ReadAsync(long subscriptionId){
            var subscriptions = await Store.GetSubscriptionsAsync(CancellationToken.None);

            return Assert.Single(subscriptions, item => item.Id == subscriptionId);
        }

        public async Task<PendingSteamReview> QueueDeliveryAsync(string? appName = "Test Game"){
            var source = CreateSource();
            var subscription = await AddActiveAsync(source, Clock.GetUtcNow().AddHours(1));
            var review = CreateReview(source, 570) with{
                AppName = appName,
            };
            Assert.True(await Store.EnqueueNewReviewsAsync(subscription.Id, [review], CancellationToken.None));

            return Assert.Single(await Store.GetPendingReviewsAsync(10, CancellationToken.None));
        }

        private ReviewMonitorService CreateMonitor() => new(Steam, Store, Notifier, Options.Create(new PhantomBotOptions{
            ReviewPollIntervalSeconds = 300,
        }), NullLogger<ReviewMonitorService>.Instance, TrackedApps, Catalog, Clock);

        public void Dispose(){
            Monitor.Dispose();

            SqliteConnection.ClearAllPools();

            try{
                Directory.Delete(_directory, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
                // Cleanup must not hide an assertion failure.
            }
        }
    }

    private sealed class TestClock : TimeProvider{
        private DateTimeOffset _now = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration){
            _now += duration;
        }

        public void SetUtcNow(DateTimeOffset value){
            if (value < _now) throw new ArgumentOutOfRangeException(nameof(value), "The test clock cannot move backwards.");

            _now = value;
        }
    }

    private sealed class FakeSteam(TestClock clock) : ISteamReviewClient{
        private readonly Dictionary<string, SteamReviewSource> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<(SteamReviewSourceKind Kind, ulong Id), Queue<ExpectedPage>> _responses = [];
        public List<(ulong SourceId, string? Cursor)> Requests{ get; } = [];
        public List<ulong> ResolvedSourceIds{ get; } = [];

        public void QueuePage(SteamReviewSource source, string? cursor, string? nextCursor, params uint[] appIds){
            QueueResponse(source, cursor, () => Task.FromResult(new SteamReviewPage([.. appIds.Select(appId => CreateReview(source, appId))], nextCursor)));
        }

        public void QueueResponse(SteamReviewSource source, string? cursor, Func<Task<SteamReviewPage>> response){
            _sources[source.Url] = source;

            var key = (source.Kind, source.Id);

            if (!_responses.TryGetValue(key, out var responses)){
                responses = new Queue<ExpectedPage>();

                _responses.Add(key, responses);
            }

            responses.Enqueue(new ExpectedPage(cursor, response));
        }

        public Task<SteamReviewSource> ResolveSourceAsync(string input, CancellationToken cancellationToken){
            cancellationToken.ThrowIfCancellationRequested();

            clock.Advance(TimeSpan.FromSeconds(2));

            var source = _sources[input];

            ResolvedSourceIds.Add(source.Id);

            return Task.FromResult(source);
        }

        public Task<SteamReviewPage> GetReviewPageAsync(SteamReviewSource source, string? cursor, CancellationToken cancellationToken){
            cancellationToken.ThrowIfCancellationRequested();

            clock.Advance(TimeSpan.FromSeconds(2));

            Requests.Add((source.Id, cursor));

            if (!_responses.TryGetValue((source.Kind, source.Id), out var responses) || responses.Count == 0) throw new InvalidOperationException("The test did not configure this Steam page request.");

            var expected = responses.Dequeue();

            Assert.Equal(expected.Cursor, cursor);

            return expected.Response();
        }

        private sealed record ExpectedPage(string? Cursor, Func<Task<SteamReviewPage>> Response);
    }

    private sealed class FakeSteamCatalogClient : ISteamCatalogClient{
        public Dictionary<uint, SteamAppMetadata> Metadata{ get; } = [];
        public List<uint[]> PicsRequests{ get; } = [];
        public Exception? PicsFailure{ get; set; }

        public Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetPicsAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
            cancellationToken.ThrowIfCancellationRequested();

            PicsRequests.Add([.. appIds]);

            if (PicsFailure is{ } failure) throw failure;

            var result = new Dictionary<uint, SteamAppMetadata>();

            foreach (var appId in appIds){
                if (Metadata.TryGetValue(appId, out var app)) result.Add(appId, app);
            }

            return Task.FromResult<IReadOnlyDictionary<uint, SteamAppMetadata>>(result);
        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<uint> GetCurrentChangeNumberAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SteamChangeSet> GetChangesSinceAsync(uint lastProcessedChangeNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken) => throw new InvalidOperationException("Review name lookup must use PICS without Store enrichment.");
    }

    private sealed class FakeNotifier : IReviewNotifier{
        public int FailuresRemaining{ get; set; }
        public int PostAttempts{ get; private set; }
        public List<PendingSteamReview> Posted{ get; } = [];

        public Task<ulong> PostAsync(
            PendingSteamReview review,
            CancellationToken cancellationToken){
            cancellationToken.ThrowIfCancellationRequested();

            PostAttempts++;

            if (FailuresRemaining > 0){
                FailuresRemaining--;

                throw new HttpRequestException("Simulated Discord send failure.");
            }

            Posted.Add(review);

            return Task.FromResult(1_000UL + (ulong)Posted.Count);
        }
    }

    private sealed class ReceiptFailureStore(IReviewSubscriptionStore inner) : IReviewSubscriptionStore{
        public int ReceiptFailuresRemaining{ get; set; }
        public int ReceiptWriteAttempts{ get; private set; }
        public int AllSubscriptionsReadCount{ get; private set; }
        public List<long[]> IdReadRequests{ get; } = [];
        public List<int> DueReadLimits{ get; } = [];

        public void ResetReadTracking(){
            AllSubscriptionsReadCount = 0;
            IdReadRequests.Clear();
            DueReadLimits.Clear();
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);

        public Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken){
            AllSubscriptionsReadCount++;

            return inner.GetSubscriptionsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsByIdsAsync(IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken){
            IdReadRequests.Add([.. subscriptionIds]);

            return inner.GetSubscriptionsByIdsAsync(subscriptionIds, cancellationToken);
        }

        public Task<IReadOnlyList<SteamReviewSubscription>> GetDueSubscriptionsAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken){
            DueReadLimits.Add(limit);

            return inner.GetDueSubscriptionsAsync(dueAt, limit, cancellationToken);
        }

        public Task<bool> TryAddAsync(SteamReviewSource source, DateTimeOffset now, CancellationToken cancellationToken) => inner.TryAddAsync(source, now, cancellationToken);
        public Task<bool> RemoveAsync(long subscriptionId, CancellationToken cancellationToken) => inner.RemoveAsync(subscriptionId, cancellationToken);
        public Task<bool> ScheduleNextPageAsync(long subscriptionId, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => inner.ScheduleNextPageAsync(subscriptionId, nextCheckUtc, cancellationToken);
        public Task<int?> SeedReviewPageAsync(long subscriptionId, IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken) => inner.SeedReviewPageAsync(subscriptionId, appIds, cancellationToken);
        public Task<bool> CompleteInitializationAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => inner.CompleteInitializationAsync(subscriptionId, source, completedUtc, nextCheckUtc, cancellationToken);
        public Task<bool> EnqueueNewReviewsAsync(long subscriptionId, IReadOnlyCollection<SteamReview> reviews, CancellationToken cancellationToken) => inner.EnqueueNewReviewsAsync(subscriptionId, reviews, cancellationToken);
        public Task<bool> CompletePollAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => inner.CompletePollAsync(subscriptionId, source, completedUtc, nextCheckUtc, cancellationToken);
        public Task<bool> RecordFailureAsync(long subscriptionId, string failureReason, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => inner.RecordFailureAsync(subscriptionId, failureReason, nextCheckUtc, cancellationToken);
        public Task<IReadOnlyList<PendingSteamReview>> GetPendingReviewsAsync(int limit, CancellationToken cancellationToken) => inner.GetPendingReviewsAsync(limit, cancellationToken);

        public Task<bool> MarkReviewPostedAsync(long pendingReviewId, ulong discordMessageId, CancellationToken cancellationToken){
            ReceiptWriteAttempts++;

            if (ReceiptFailuresRemaining <= 0) return inner.MarkReviewPostedAsync(pendingReviewId, discordMessageId, cancellationToken);

            ReceiptFailuresRemaining--;

            throw new IOException("Simulated receipt write failure.");
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment{
        public string EnvironmentName{ get; set; } = Environments.Development;
        public string ApplicationName{ get; set; } = "PhantomBot.Tests";
        public string ContentRootPath{ get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider{ get; set; } = new NullFileProvider();
    }
}