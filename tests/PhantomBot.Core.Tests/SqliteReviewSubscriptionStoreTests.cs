using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;
using PhantomBot.Infrastructure.Persistence;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SqliteReviewSubscriptionStoreTests{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryAddCreatesImmediatelyDueInitializingSubscription(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var createdUtc = Now.ToOffset(TimeSpan.FromHours(2));

        Assert.True(await database.Store.TryAddAsync(source, createdUtc, CancellationToken.None));

        var subscription = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.True(subscription.Id > 0);
        Assert.Equal(source, subscription.Source);
        Assert.Equal(SteamReviewSubscriptionStatus.Initializing, subscription.Status);
        Assert.Equal(Now, subscription.CreatedUtc);
        Assert.Equal(TimeSpan.Zero, subscription.CreatedUtc.Offset);
        Assert.Equal(Now, subscription.NextCheckUtc);
        Assert.Null(subscription.LastSuccessfulCheckUtc);
        Assert.Equal(0, subscription.ConsecutiveFailures);
        Assert.Null(subscription.LastError);
        Assert.Empty(await database.Store.GetDueSubscriptionsAsync(Now.AddTicks(-1), 10, CancellationToken.None));

        var due = Assert.Single(await database.Store.GetDueSubscriptionsAsync(Now, 10, CancellationToken.None));

        Assert.Equal(subscription, due);
    }

    [Fact]
    public async Task TryAddRejectsDuplicateIdentityWithoutChangingExistingSubscription(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();

        Assert.True(await database.Store.TryAddAsync(source, Now, CancellationToken.None));

        var original = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.False(await database.Store.TryAddAsync(source with{
            Name = "A changed display name",
        }, Now.AddMinutes(5), CancellationToken.None));

        var actual = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(original, actual);
    }

    [Fact]
    public async Task DueSubscriptionsUseUtcOrderingAndRespectLimit(){
        using var database = await TestDatabase.CreateAsync();

        var later = CreateSource();
        var earlier = CreateSource(76_561_198_297_114_543UL);
        var future = CreateSource(76_561_198_297_114_544UL);

        Assert.True(await database.Store.TryAddAsync(later, Now.AddMinutes(5).ToOffset(TimeSpan.FromHours(-4)), CancellationToken.None));
        Assert.True(await database.Store.TryAddAsync(earlier, Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(8)), CancellationToken.None));
        Assert.True(await database.Store.TryAddAsync(future, Now.AddMinutes(10), CancellationToken.None));

        var limited = Assert.Single(await database.Store.GetDueSubscriptionsAsync(Now.AddMinutes(5), 1, CancellationToken.None));

        Assert.Equal(earlier, limited.Source);

        var due = await database.Store.GetDueSubscriptionsAsync(Now.AddMinutes(5), 10, CancellationToken.None);

        Assert.Collection(due, subscription => Assert.Equal(earlier, subscription.Source), subscription => Assert.Equal(later, subscription.Source));
    }

    [Fact]
    public async Task GetSubscriptionsByIdsReturnsOnlyRequestedExistingSubscriptions(){
        using var database = await TestDatabase.CreateAsync();

        var initializing = await AddSubscriptionAsync(database.Store, CreateSource());
        var active = await AddActiveSubscriptionAsync(database.Store, CreateSource(76_561_198_297_114_543UL));

        await AddSubscriptionAsync(database.Store, CreateSource(76_561_198_297_114_544UL));

        Assert.Empty(await database.Store.GetSubscriptionsByIdsAsync([], CancellationToken.None));

        var selected = await database.Store.GetSubscriptionsByIdsAsync([active.Id, initializing.Id, active.Id, long.MaxValue], CancellationToken.None);

        Assert.Collection(selected, subscription => Assert.Equal(initializing, subscription), subscription => Assert.Equal(active, subscription));
        Assert.True(await database.Store.RemoveAsync(initializing.Id, CancellationToken.None));

        var remaining = Assert.Single(await database.Store.GetSubscriptionsByIdsAsync([initializing.Id, active.Id], CancellationToken.None));

        Assert.Equal(active, remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduleNextPageChangesOnlyDueTimeAndPreservesFailureHistory(bool active){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();

        var subscription = active ? await AddActiveSubscriptionAsync(database.Store, source) : await AddSubscriptionAsync(database.Store, source);

        Assert.True(await database.Store.RecordFailureAsync(subscription.Id, "First failure", Now.AddMinutes(30), CancellationToken.None));
        Assert.True(await database.Store.RecordFailureAsync(subscription.Id, "Second failure", Now.AddHours(1), CancellationToken.None));

        var before = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(2, before.ConsecutiveFailures);
        Assert.Equal("Second failure", before.LastError);

        var nextCheckUtc = Now.AddHours(1).AddSeconds(1);

        Assert.True(await database.Store.ScheduleNextPageAsync(subscription.Id, nextCheckUtc.ToOffset(TimeSpan.FromHours(3)), CancellationToken.None));

        var after = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(before with{
            NextCheckUtc = nextCheckUtc,
        }, after);

        Assert.Equal(TimeSpan.Zero, after.NextCheckUtc.Offset);
        Assert.Empty(await database.Store.GetDueSubscriptionsAsync(nextCheckUtc.AddTicks(-1), 10, CancellationToken.None));

        var due = Assert.Single(await database.Store.GetDueSubscriptionsAsync(nextCheckUtc, 10, CancellationToken.None));

        Assert.Equal(after, due);

        var reopened = database.CreateStore();

        await reopened.InitializeAsync(CancellationToken.None);

        var persisted = Assert.Single(await reopened.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(after, persisted);
    }

    [Fact]
    public async Task SeedingCountsOnlyNewAppIdsAndNeverQueuesHistoricalReviews(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var subscription = await AddSubscriptionAsync(database.Store, source);

        Assert.False(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [CreateReview(source, 570)], CancellationToken.None));
        Assert.Equal(2, await database.Store.SeedReviewPageAsync(subscription.Id, [570, 570, 730], CancellationToken.None));
        Assert.Equal(1, await database.Store.SeedReviewPageAsync(subscription.Id, [730, 440], CancellationToken.None));
        Assert.Equal(0, await database.Store.SeedReviewPageAsync(subscription.Id, [], CancellationToken.None));
        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
        Assert.True(await database.Store.CompleteInitializationAsync(subscription.Id, source, Now.AddMinutes(1), Now.AddMinutes(6), CancellationToken.None));
        Assert.Null(await database.Store.SeedReviewPageAsync(subscription.Id, [10], CancellationToken.None));
        Assert.False(await database.Store.CompleteInitializationAsync(subscription.Id, source, Now.AddMinutes(2), Now.AddMinutes(7), CancellationToken.None));
        Assert.True(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [CreateReview(source, 570), CreateReview(source, 730), CreateReview(source, 440),], CancellationToken.None));
        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
        Assert.True(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [CreateReview(source, 10)], CancellationToken.None));

        var pending = Assert.Single(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));

        Assert.Equal(10U, pending.Review.AppId);
    }

    [Fact]
    public async Task InvalidSeedPageRollsBackEarlierInserts(){
        using var database = await TestDatabase.CreateAsync();

        var subscription = await AddSubscriptionAsync(database.Store, CreateSource());

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.SeedReviewPageAsync(subscription.Id, [570, 0], CancellationToken.None));

        Assert.Equal(1, await database.Store.SeedReviewPageAsync(subscription.Id, [570], CancellationToken.None));
        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingInitializingSubscriptionDeletesPartialSeedHistory(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var original = await AddSubscriptionAsync(database.Store, source);

        Assert.Equal(2, await database.Store.SeedReviewPageAsync(original.Id, [570, 730], CancellationToken.None));
        Assert.True(await database.Store.RemoveAsync(original.Id, CancellationToken.None));
        Assert.Equal(0L, await database.CountSubscriptionRowsAsync(original.Id));

        var replacement = await AddSubscriptionAsync(database.Store, source);

        Assert.True(replacement.Id > original.Id);
        Assert.Null(await database.Store.SeedReviewPageAsync(original.Id, [440], CancellationToken.None));
        Assert.False(await database.Store.ScheduleNextPageAsync(original.Id, Now.AddHours(1), CancellationToken.None));

        var actual = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(replacement, actual);
        Assert.Equal(3, await database.Store.SeedReviewPageAsync(replacement.Id, [570, 730, 440], CancellationToken.None));
        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task QueuedReviewsRoundTripAndRemainDeduplicatedAfterPosting(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var subscription = await AddActiveSubscriptionAsync(database.Store, source);

        var firstReview = CreateReview(source, 570);
        var secondReview = CreateReview(source, 730);

        Assert.True(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [firstReview, firstReview with{ Text = "An edited review must not replace the queued original.", }, secondReview,], CancellationToken.None));

        var pending = await database.Store.GetPendingReviewsAsync(10, CancellationToken.None);

        // ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
        Assert.Collection(pending,
            item => {
                Assert.Equal(subscription.Id, item.SubscriptionId);
                Assert.Equal(firstReview, item.Review);
            },
            item => {
                Assert.Equal(subscription.Id, item.SubscriptionId);
                Assert.Equal(secondReview, item.Review);
            });

        Assert.True(pending[0].Id > 0);
        Assert.True(pending[1].Id > pending[0].Id);

        var limited = Assert.Single(await database.Store.GetPendingReviewsAsync(1, CancellationToken.None));

        Assert.Equal(pending[0], limited);

        var reopened = database.CreateStore();

        await reopened.InitializeAsync(CancellationToken.None);
        await reopened.InitializeAsync(CancellationToken.None);

        var persisted = await reopened.GetPendingReviewsAsync(10, CancellationToken.None);

        Assert.Collection(persisted, item => Assert.Equal(pending[0], item), item => Assert.Equal(pending[1], item));
        Assert.True(await reopened.MarkReviewPostedAsync(pending[0].Id, 123_456_789, CancellationToken.None));
        Assert.False(await reopened.MarkReviewPostedAsync(pending[0].Id, 987_654_321, CancellationToken.None));
        Assert.True(await reopened.EnqueueNewReviewsAsync(subscription.Id, [firstReview, secondReview], CancellationToken.None));

        var remaining = Assert.Single(await reopened.GetPendingReviewsAsync(10, CancellationToken.None));

        Assert.Equal(pending[1], remaining);
    }

    [Fact]
    public async Task MismatchedReviewSourceRollsBackSeenIdsAndQueuedPosts(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var otherSource = CreateSource(76_561_198_297_114_543UL);

        var subscription = await AddActiveSubscriptionAsync(database.Store, source);
        var validReview = CreateReview(source, 570);

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.EnqueueNewReviewsAsync(subscription.Id, [validReview, CreateReview(otherSource, 730),], CancellationToken.None));

        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
        Assert.True(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [validReview], CancellationToken.None));

        var pending = Assert.Single(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));

        Assert.Equal(validReview, pending.Review);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailuresPreserveStatusAndSuccessfulCheckClearsFailureState(bool active){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var subscription = await AddSubscriptionAsync(database.Store, source);

        Assert.Equal(1, await database.Store.SeedReviewPageAsync(subscription.Id, [570], CancellationToken.None));

        if (active) Assert.True(await database.Store.CompleteInitializationAsync(subscription.Id, source, Now.AddMinutes(1), Now.AddMinutes(6), CancellationToken.None));

        var nextCheck = Now.AddHours(1);

        Assert.True(await database.Store.RecordFailureAsync(subscription.Id, "First failure", Now.AddMinutes(30), CancellationToken.None));
        Assert.True(await database.Store.RecordFailureAsync(subscription.Id, "Second failure", nextCheck.ToOffset(TimeSpan.FromHours(3)), CancellationToken.None));

        var failed = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(active ? SteamReviewSubscriptionStatus.Active : SteamReviewSubscriptionStatus.Initializing, failed.Status);
        Assert.Equal(2, failed.ConsecutiveFailures);
        Assert.Equal("Second failure", failed.LastError);
        Assert.Equal(nextCheck, failed.NextCheckUtc);
        Assert.Equal(TimeSpan.Zero, failed.NextCheckUtc.Offset);
        Assert.Equal(active ? Now.AddMinutes(1) : null, failed.LastSuccessfulCheckUtc);
        Assert.Empty(await database.Store.GetDueSubscriptionsAsync(nextCheck.AddTicks(-1), 10, CancellationToken.None));
        Assert.Single(await database.Store.GetDueSubscriptionsAsync(nextCheck, 10, CancellationToken.None));

        if (!active) Assert.Equal(0, await database.Store.SeedReviewPageAsync(subscription.Id, [570], CancellationToken.None));

        var refreshedSource = source with{
            Name = "Updated Reviewer Name",
        };

        var completedUtc = nextCheck.AddMinutes(1);
        var nextPoll = completedUtc.AddMinutes(5);

        var completed = active ? await database.Store.CompletePollAsync(subscription.Id, refreshedSource, completedUtc, nextPoll, CancellationToken.None) : await database.Store.CompleteInitializationAsync(subscription.Id, refreshedSource, completedUtc, nextPoll, CancellationToken.None);

        Assert.True(completed);

        var actual = Assert.Single(await database.Store.GetSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(SteamReviewSubscriptionStatus.Active, actual.Status);
        Assert.Equal(refreshedSource, actual.Source);
        Assert.Equal(subscription.CreatedUtc, actual.CreatedUtc);
        Assert.Equal(completedUtc, actual.LastSuccessfulCheckUtc);
        Assert.Equal(nextPoll, actual.NextCheckUtc);
        Assert.Equal(0, actual.ConsecutiveFailures);
        Assert.Null(actual.LastError);
        Assert.True(await database.Store.EnqueueNewReviewsAsync(subscription.Id, [CreateReview(refreshedSource, 570)], CancellationToken.None));
        Assert.Empty(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task RemovalCascadesAndStaleWorkerCallsCannotAffectReaddedSubscription(){
        using var database = await TestDatabase.CreateAsync();

        var source = CreateSource();
        var original = await AddSubscriptionAsync(database.Store, source);

        Assert.Equal(1, await database.Store.SeedReviewPageAsync(original.Id, [730], CancellationToken.None));
        Assert.True(await database.Store.CompleteInitializationAsync(original.Id, source, Now.AddMinutes(1), Now.AddMinutes(6), CancellationToken.None));

        var review = CreateReview(source, 570);

        Assert.True(await database.Store.EnqueueNewReviewsAsync(original.Id, [review], CancellationToken.None));

        var oldPending = Assert.Single(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));
        var otherSource = CreateSource(76_561_198_297_114_543UL);
        var other = await AddActiveSubscriptionAsync(database.Store, otherSource);

        Assert.True(await database.Store.EnqueueNewReviewsAsync(other.Id, [CreateReview(otherSource, 570)], CancellationToken.None));
        Assert.True(await database.Store.RemoveAsync(original.Id, CancellationToken.None));
        Assert.False(await database.Store.RemoveAsync(original.Id, CancellationToken.None));
        Assert.Equal(0L, await database.CountSubscriptionRowsAsync(original.Id));

        var unaffected = Assert.Single(await database.Store.GetPendingReviewsAsync(10, CancellationToken.None));

        Assert.Equal(other.Id, unaffected.SubscriptionId);

        var replacement = await AddSubscriptionAsync(database.Store, source);

        Assert.True(replacement.Id > original.Id);
        Assert.Null(await database.Store.SeedReviewPageAsync(original.Id, [570], CancellationToken.None));
        Assert.False(await database.Store.CompleteInitializationAsync(original.Id, source, Now.AddMinutes(2), Now.AddMinutes(7), CancellationToken.None));
        Assert.False(await database.Store.EnqueueNewReviewsAsync(original.Id, [review], CancellationToken.None));
        Assert.False(await database.Store.CompletePollAsync(original.Id, source, Now.AddMinutes(2), Now.AddMinutes(7), CancellationToken.None));
        Assert.False(await database.Store.RecordFailureAsync(original.Id, "A stale worker failure", Now.AddHours(1), CancellationToken.None));
        Assert.False(await database.Store.MarkReviewPostedAsync(oldPending.Id, 123_456_789, CancellationToken.None));
        Assert.False(await database.Store.ScheduleNextPageAsync(original.Id, Now.AddHours(2), CancellationToken.None));
        Assert.Empty(await database.Store.GetSubscriptionsByIdsAsync([original.Id], CancellationToken.None));

        var subscriptions = await database.Store.GetSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(replacement, Assert.Single(subscriptions, item => item.Id == replacement.Id));
        Assert.Equal(1, await database.Store.SeedReviewPageAsync(replacement.Id, [730], CancellationToken.None));
        Assert.True(await database.Store.CompleteInitializationAsync(replacement.Id, source, Now.AddMinutes(2), Now.AddMinutes(7), CancellationToken.None));
        Assert.True(await database.Store.EnqueueNewReviewsAsync(replacement.Id, [review], CancellationToken.None));

        var pending = await database.Store.GetPendingReviewsAsync(10, CancellationToken.None);

        Assert.Equal(2, pending.Count);

        var newPending = Assert.Single(pending, item => item.SubscriptionId == replacement.Id);

        Assert.True(newPending.Id > oldPending.Id);
        Assert.Equal(review, newPending.Review);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static async Task<SteamReviewSubscription> AddSubscriptionAsync(SqliteReviewSubscriptionStore store, SteamReviewSource source){
        Assert.True(await store.TryAddAsync(source, Now, CancellationToken.None));

        var subscriptions = await store.GetSubscriptionsAsync(CancellationToken.None);

        return Assert.Single(subscriptions, subscription => subscription.Source.Kind == source.Kind && subscription.Source.Id == source.Id);
    }

    private static async Task<SteamReviewSubscription> AddActiveSubscriptionAsync(SqliteReviewSubscriptionStore store, SteamReviewSource source){
        var subscription = await AddSubscriptionAsync(store, source);

        Assert.True(await store.CompleteInitializationAsync(subscription.Id, source, Now.AddMinutes(1), Now.AddMinutes(6), CancellationToken.None));

        return Assert.Single(await store.GetSubscriptionsAsync(CancellationToken.None), item => item.Id == subscription.Id);
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
        Recommendation = SteamReviewRecommendation.NotRecommended,
        Text = "A complete review payload.",
        Url = $"{source.Url}recommended/{appId.ToString(CultureInfo.InvariantCulture)}/",
        ExternalReviewUrl = "https://example.com/full-review",
        ThumbnailUrl = "https://example.com/capsule.jpg",
        PublishedUtc = Now.AddDays(-1),
    };

    private sealed class TestDatabase : IDisposable{
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhantomBotReviewTests-{Guid.NewGuid():N}");
        private readonly string _databasePath;
        public SqliteReviewSubscriptionStore Store{ get; }

        private TestDatabase(){
            Directory.CreateDirectory(_directory);

            _databasePath = Path.Combine(_directory, "phantombot.db");

            Store = CreateStore();
        }

        public static async Task<TestDatabase> CreateAsync(){
            var database = new TestDatabase();

            try{
                await database.Store.InitializeAsync(CancellationToken.None);

                return database;
            }
            catch{
                database.Dispose();

                throw;
            }
        }

        public SqliteReviewSubscriptionStore CreateStore() => new(Options.Create(new PhantomBotOptions{
            DatabasePath = _databasePath,
        }), new TestHostEnvironment(_directory));

        public async Task<long> CountSubscriptionRowsAsync(long subscriptionId){
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder{
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();

            command.CommandText = """
                                  SELECT
                                      (SELECT COUNT(*) FROM review_subscriptions WHERE id = $id)
                                      + (SELECT COUNT(*) FROM review_seen_apps WHERE subscription_id = $id)
                                      + (SELECT COUNT(*) FROM review_outbox WHERE subscription_id = $id);
                                  """;

            command.Parameters.AddWithValue("$id", subscriptionId);

            return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        public void Dispose(){
            SqliteConnection.ClearAllPools();

            try{
                Directory.Delete(_directory, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
                // Cleanup must not hide an assertion failure.
            }
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment{
        public string EnvironmentName{ get; set; } = Environments.Development;
        public string ApplicationName{ get; set; } = "PhantomBot.Tests";
        public string ContentRootPath{ get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider{ get; set; } = new NullFileProvider();
    }
}