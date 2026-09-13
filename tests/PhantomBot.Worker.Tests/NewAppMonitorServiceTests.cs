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

// ReSharper disable once MemberCanBeFileLocal
public sealed class NewAppMonitorServiceTests{
    private const uint AppId = 5_000_001;

    [Theory]
    [InlineData(SteamAppKind.Game)]
    [InlineData(SteamAppKind.Tool)]
    public async Task RestartDoesNotPostTrackedAppAgain(SteamAppKind kind){
        using var test = await TestContext.CreateAsync();

        var metadata = new SteamAppMetadata(AppId, "Restart Test App", kind, 101);

        test.Steam.SetResponse(101, [AppId], metadata);

        Assert.True(await test.RunAsync());
        Assert.Equal(metadata, Assert.Single(test.NewNotifier.Posted));
        Assert.Equal(101U, await test.Store.GetLastChangeNumberAsync(CancellationToken.None));

        test.Steam.SetResponse(101, []);

        Assert.True(await test.RunAsync());
        Assert.Single(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal([100U, 101U], test.Steam.RequestedChangeNumbers.ToArray());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.Announced, tracked.Status);
        Assert.Equal(kind, tracked.Kind);
        Assert.False(tracked.IsRetired);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, tracked.DiscordMessageId);
    }

    [Theory]
    [InlineData(SteamAppKind.Game, false)]
    [InlineData(SteamAppKind.Game, true)]
    [InlineData(SteamAppKind.Tool, false)]
    [InlineData(SteamAppKind.Tool, true)]
    public async Task PendingAppIsPostedWhenMetadataRetrySucceeds(SteamAppKind kind, bool isRetired){
        using var test = await TestContext.CreateAsync();

        test.Steam.SetResponse(101, [AppId]);

        Assert.True(await test.RunAsync());

        var pending = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.PendingMetadata, pending.Status);
        Assert.Null(pending.DiscordMessageId);
        Assert.Empty(test.NewNotifier.Posted);

        await test.Store.UpsertAppAsync(pending with{
            NextMetadataCheckUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Resolved Test App", kind, 102){
            IsRetired = isRetired,
        };

        test.Steam.SetResponse(102, [], metadata);

        Assert.True(await test.RunAsync());

        var resolved = await test.ReadAppAsync();

        Assert.Equal(metadata, Assert.Single(test.NewNotifier.Posted));
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Equal(isRetired ? 1 : 0, test.RetiredNotifier.Posted.Count);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(TrackingStatus.Announced, resolved.Status);
        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, resolved.DiscordMessageId);
        Assert.Equal(test.RetiredNotifier.LastPostedMessageId, resolved.RetirementDiscordMessageId);
        Assert.Equal(isRetired, resolved.IsRetired);
        Assert.Null(resolved.NextMetadataCheckUtc);
        Assert.Equal(102U, await test.Store.GetLastChangeNumberAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ContinuityLossDoesNotAdvanceCheckpoint(){
        using var test = await TestContext.CreateAsync();

        test.Steam.SetResponse(500, [AppId], new SteamAppMetadata(AppId, "Test Game", SteamAppKind.Game, 500));
        test.Steam.RequiresFullAppUpdate = true;

        Assert.False(await test.RunAsync());
        Assert.Equal(100U, Assert.Single(test.Steam.RequestedChangeNumbers));
        Assert.Empty(test.Steam.PicsRequests);
        Assert.Empty(test.Steam.EnrichedRequests);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(100U, await test.Store.GetLastChangeNumberAsync(CancellationToken.None));
        Assert.Equal(0L, await test.Store.CountAppsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(SteamAppKind.Other, false)]
    [InlineData(SteamAppKind.Other, true)]
    [InlineData(SteamAppKind.Application, false)]
    [InlineData(SteamAppKind.Application, true)]
    public async Task TerminalKindsNeverReceiveNewAppNotifications(SteamAppKind kind, bool isRetired){
        using var test = await TestContext.CreateAsync();

        test.Options.Value.PostUnknownApps = true;

        var metadata = new SteamAppMetadata(AppId, "Test App", kind, 101){
            IsRetired = isRetired,
        };

        test.Steam.SetResponse(101, [AppId], metadata);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Equal(isRetired ? 1 : 0, test.RetiredNotifier.Posted.Count);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(TrackingStatus.Ignored, tracked.Status);
        Assert.Equal(kind, tracked.Kind);
        Assert.Equal(isRetired, tracked.IsRetired);
        Assert.Null(tracked.DiscordMessageId);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Equal(test.RetiredNotifier.LastPostedMessageId, tracked.RetirementDiscordMessageId);
    }

    [Theory]
    [InlineData(SteamAppKind.Game)]
    [InlineData(SteamAppKind.Dlc)]
    [InlineData(SteamAppKind.Beta)]
    [InlineData(SteamAppKind.Music)]
    [InlineData(SteamAppKind.Demo)]
    [InlineData(SteamAppKind.Hardware)]
    [InlineData(SteamAppKind.Tool)]
    public async Task FirstSeenRetiredWantedAppReceivesBothNotifications(SteamAppKind kind){
        using var test = await TestContext.CreateAsync();

        var metadata = new SteamAppMetadata(AppId, "New Retired App", kind, 101){
            IsRetired = true,
        };

        test.Steam.SetResponse(101, [AppId], metadata);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(metadata, Assert.Single(test.NewNotifier.Posted));
        Assert.Equal(metadata, Assert.Single(test.RetiredNotifier.Posted));
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(TrackingStatus.Announced, tracked.Status);
        Assert.Equal(kind, tracked.Kind);
        Assert.True(tracked.IsRetired);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, tracked.DiscordMessageId);
        Assert.Equal(test.RetiredNotifier.LastPostedMessageId, tracked.RetirementDiscordMessageId);
    }

    [Fact]
    public async Task PreviouslyIgnoredToolIsAnnouncedWhenKindBecomesWanted(){
        using var test = await TestContext.CreateAsync();

        var original = CreateTrackedApp(TrackingStatus.Ignored) with{
            Name = "Previously Ignored Tool",
            Kind = SteamAppKind.Other,
        };

        await test.Store.UpsertAppAsync(original, CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Recognized Tool", SteamAppKind.Tool, 101);

        test.Steam.SetResponse(101, [AppId], metadata);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(metadata, Assert.Single(test.NewNotifier.Posted));
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(TrackingStatus.Announced, tracked.Status);
        Assert.Equal(SteamAppKind.Tool, tracked.Kind);
        Assert.Equal(metadata.Name, tracked.Name);
        Assert.Equal(original.FirstSeenChange, tracked.FirstSeenChange);
        Assert.Equal(101U, tracked.LastSeenChange);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, tracked.DiscordMessageId);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Empty(test.Steam.PicsRequests);
        Assert.Equal([AppId], Assert.Single(test.Steam.EnrichedRequests));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NewerMetadataForSeededIncompleteAppIsAnnounced(bool includedInChangeSet, bool isRetired){
        using var test = await TestContext.CreateAsync();

        await test.Store.SeedAppsAsync([new SteamAppListEntry(AppId, null)], 100, CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Newly Revealed Game", SteamAppKind.Game, 101){
            IsRetired = isRetired,
        };

        uint[] changedIds = includedInChangeSet ? [AppId] : [];

        test.Steam.SetResponse(101, changedIds, metadata);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.Announced, tracked.Status);
        Assert.Equal(100U, tracked.FirstSeenChange);
        Assert.Equal(101U, tracked.LastSeenChange);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, tracked.DiscordMessageId);
        Assert.Equal(test.RetiredNotifier.LastPostedMessageId, tracked.RetirementDiscordMessageId);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Equal(metadata, Assert.Single(test.NewNotifier.Posted));
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Equal(isRetired ? 1 : 0, test.RetiredNotifier.Posted.Count);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Empty(test.Steam.PicsRequests);
        Assert.Single(test.Steam.EnrichedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineAgeMetadataResolvesWithoutNotification(bool isRetired){
        using var test = await TestContext.CreateAsync();

        await test.Store.SeedAppsAsync([new SteamAppListEntry(AppId, null)], 100, CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Historical Game", SteamAppKind.Game, 100){
            IsRetired = isRetired,
        };

        test.Steam.SetResponse(100, [], metadata);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.Seeded, tracked.Status);
        Assert.Equal(100U, tracked.FirstSeenChange);
        Assert.Equal(100U, tracked.LastSeenChange);
        Assert.Equal(isRetired, tracked.IsRetired);
        Assert.Null(tracked.DiscordMessageId);
        Assert.Null(tracked.RetirementDiscordMessageId);
        Assert.Null(tracked.NextMetadataCheckUtc);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
    }

    [Fact]
    public async Task MissingMetadataPreservesSeededIncompleteOrigin(){
        using var test = await TestContext.CreateAsync();

        await test.Store.SeedAppsAsync([new SteamAppListEntry(AppId, null)], 100, CancellationToken.None);

        test.Steam.SetResponse(100, []);

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.SeededIncomplete, tracked.Status);
        Assert.Null(tracked.DiscordMessageId);
        Assert.NotNull(tracked.NextMetadataCheckUtc);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
    }

    [Fact]
    public async Task ReplacementMessageIdIsPersisted(){
        using var test = await TestContext.CreateAsync();

        await test.Store.UpsertAppAsync(CreateTrackedApp(TrackingStatus.Announced, newMessageId: 10), CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Updated Game", SteamAppKind.Game, 101);

        test.Steam.SetResponse(101, [AppId], metadata);
        test.NewNotifier.UpdateResult = 20;

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();
        var update = Assert.Single(test.NewNotifier.Updated);

        Assert.Equal(10UL, update.MessageId);
        Assert.Equal(metadata, update.App);
        Assert.Equal(20UL, tracked.DiscordMessageId);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Posted);
    }

    [Theory]
    [InlineData(SteamAppKind.Game, false)]
    [InlineData(SteamAppKind.Game, true)]
    [InlineData(SteamAppKind.Tool, false)]
    [InlineData(SteamAppKind.Tool, true)]
    public async Task OrdinarySeededChangeUsesPicsWithoutEnrichmentOrRowChanges(SteamAppKind kind, bool metadataMissing){
        using var test = await TestContext.CreateAsync();

        var original = CreateTrackedApp(TrackingStatus.Seeded) with{
            Kind = kind,
        };

        await test.Store.UpsertAppAsync(original, CancellationToken.None);

        SteamAppMetadata[] metadata = metadataMissing ? [] : [new SteamAppMetadata(AppId, "Changed Historical Name", kind, 101)];

        test.Steam.SetResponse(101, [AppId], metadata);

        Assert.True(await test.RunAsync());
        Assert.Equal(original, await test.ReadAppAsync());
        Assert.Equal([AppId], Assert.Single(test.Steam.PicsRequests));
        Assert.Empty(test.Steam.EnrichedRequests);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
        Assert.Equal(101U, await test.Store.GetLastChangeNumberAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HistoricalRetirementEnrichesOnlyRetiredCandidates(){
        const uint activeAppId = AppId + 1;
        using var test = await TestContext.CreateAsync();

        await test.Store.SeedAppsAsync(
        [
            new SteamAppListEntry(AppId, "Historical Application", "application"),
            new SteamAppListEntry(activeAppId, "Historical Game", "game"),
        ], 100, CancellationToken.None);

        var before = await test.Store.GetAppsAsync([activeAppId], CancellationToken.None);

        var retired = new SteamAppMetadata(AppId, "Retired Application", SteamAppKind.Application, 101){
            IsRetired = true,
        };

        var enriched = retired with{
            Description = "Preserved Store description.",
            ThumbnailUrl = "https://example.com/header.jpg",
        };

        test.Steam.SetResponse(101, [AppId, activeAppId], retired, new SteamAppMetadata(activeAppId, "Changed Game", SteamAppKind.Game, 101));

        test.Steam.EnrichedMetadata = new Dictionary<uint, SteamAppMetadata>{
            [AppId] = enriched,
        };

        Assert.True(await test.RunAsync());
        Assert.Equal([AppId, activeAppId], Assert.Single(test.Steam.PicsRequests).Order().ToArray());
        Assert.Equal([AppId], Assert.Single(test.Steam.EnrichedRequests));
        Assert.Equal(enriched, Assert.Single(test.RetiredNotifier.Posted));
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);

        var tracked = await test.ReadAppAsync();
        var after = await test.Store.GetAppsAsync([activeAppId], CancellationToken.None);

        Assert.Equal(before[activeAppId], after[activeAppId]);
        Assert.Equal(TrackingStatus.Seeded, tracked.Status);
        Assert.True(tracked.IsRetired);
        Assert.Null(tracked.DiscordMessageId);
        Assert.Equal(test.RetiredNotifier.LastPostedMessageId, tracked.RetirementDiscordMessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalRetirementFallsBackToPicsWhenEnrichmentIsMissingOrDisagrees(
        bool returnsActiveMetadata){
        using var test = await TestContext.CreateAsync();

        await test.Store.UpsertAppAsync(CreateTrackedApp(TrackingStatus.Seeded), CancellationToken.None);

        var retired = new SteamAppMetadata(AppId, "Retired Game", SteamAppKind.Game, 101){
            IsRetired = true,
        };

        test.Steam.SetResponse(101, [AppId], retired);

        test.Steam.EnrichedMetadata = returnsActiveMetadata
            ? new Dictionary<uint, SteamAppMetadata>{
                [AppId] = retired with{
                    IsRetired = false,
                },
            }
            : new Dictionary<uint, SteamAppMetadata>();

        Assert.True(await test.RunAsync());
        Assert.Equal(retired, Assert.Single(test.RetiredNotifier.Posted));
        Assert.True((await test.ReadAppAsync()).IsRetired);
        Assert.Empty(test.NewNotifier.Posted);
    }

    [Fact]
    public async Task RetirementAlreadyPresentInBaselineIsNotAnnouncedOnLaterChange(){
        using var test = await TestContext.CreateAsync();

        await test.Store.SeedAppsAsync([new SteamAppListEntry(AppId, "Historical Retired Game", "game", true)], 100, CancellationToken.None);

        test.Steam.SetResponse(101, [AppId], new SteamAppMetadata(AppId, "Historical Retired Game", SteamAppKind.Game, 101){
            IsRetired = true,
        });

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.True(tracked.IsRetired);
        Assert.Equal(101U, tracked.LastSeenChange);
        Assert.Null(tracked.RetirementDiscordMessageId);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);
    }

    [Theory]
    [InlineData(false, 110U)]
    [InlineData(false, 111U)]
    [InlineData(true, 110U)]
    [InlineData(true, 111U)]
    public async Task ExistingRetirementMessageUpdatesOnlyForNewerMetadata(bool isRetired, uint metadataChange){
        using var test = await TestContext.CreateAsync();

        await test.Store.UpsertAppAsync(CreateTrackedApp(TrackingStatus.Announced, isRetired, 10, 20, 110), CancellationToken.None);

        await test.Store.SetLastChangeNumberAsync(110, CancellationToken.None);

        var metadata = new SteamAppMetadata(AppId, "Retired Game", SteamAppKind.Game, metadataChange){
            IsRetired = true,
        };

        test.Steam.SetResponse(111, [AppId], metadata);
        test.RetiredNotifier.UpdateResult = 30;

        Assert.True(await test.RunAsync());

        var tracked = await test.ReadAppAsync();

        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Equal(10UL, tracked.DiscordMessageId);
        Assert.True(tracked.IsRetired);
        Assert.Equal(metadataChange, tracked.LastSeenChange);

        if (metadataChange > 110){
            var update = Assert.Single(test.RetiredNotifier.Updated);

            Assert.Equal(20UL, update.MessageId);
            Assert.Equal(metadata, update.App);
            Assert.Equal(30UL, tracked.RetirementDiscordMessageId);
        }
        else{
            Assert.Empty(test.RetiredNotifier.Updated);
            Assert.Equal(20UL, tracked.RetirementDiscordMessageId);
        }
    }

    [Fact]
    public async Task HistoricalRetirementReversalPreservesMessageIdAndReusesItOnRetirement(){
        using var test = await TestContext.CreateAsync();

        await test.Store.UpsertAppAsync(CreateTrackedApp(TrackingStatus.Seeded, true, retirementMessageId: 20, lastChange: 110), CancellationToken.None);
        await test.Store.SetLastChangeNumberAsync(110, CancellationToken.None);

        test.Steam.SetResponse(111, [AppId], new SteamAppMetadata(AppId, null, SteamAppKind.Unknown, 111));

        Assert.True(await test.RunAsync());

        var restored = await test.ReadAppAsync();

        Assert.False(restored.IsRetired);
        Assert.Equal("Stored Game", restored.Name);
        Assert.Equal(SteamAppKind.Game, restored.Kind);
        Assert.Equal(TrackingStatus.Seeded, restored.Status);
        Assert.Equal(20UL, restored.RetirementDiscordMessageId);
        Assert.Equal(111U, restored.LastSeenChange);
        Assert.Empty(test.Steam.EnrichedRequests);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.RetiredNotifier.Updated);

        test.Steam.SetResponse(112, [AppId], new SteamAppMetadata(AppId, "Retired Again", SteamAppKind.Game, 112){
            IsRetired = true,
        });

        test.RetiredNotifier.UpdateResult = 30;

        Assert.True(await test.RunAsync());

        var retiredAgain = await test.ReadAppAsync();

        Assert.True(retiredAgain.IsRetired);
        Assert.Equal(30UL, retiredAgain.RetirementDiscordMessageId);
        Assert.Equal(20UL, Assert.Single(test.RetiredNotifier.Updated).MessageId);
        Assert.Empty(test.RetiredNotifier.Posted);
        Assert.Empty(test.NewNotifier.Posted);
        Assert.Empty(test.NewNotifier.Updated);
    }

    [Fact]
    public async Task IncompleteRetiredAppKeepsRetryAndLaterReceivesNewAppNotification(){
        using var test = await TestContext.CreateAsync();

        test.Steam.SetResponse(101, [AppId], new SteamAppMetadata(AppId, null, SteamAppKind.Unknown, 101){
            IsRetired = true,
        });

        Assert.True(await test.RunAsync());

        var pending = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.PendingMetadata, pending.Status);
        Assert.NotNull(pending.NextMetadataCheckUtc);
        Assert.Null(pending.DiscordMessageId);
        Assert.Single(test.RetiredNotifier.Posted);
        Assert.Empty(test.NewNotifier.Posted);

        await test.Store.UpsertAppAsync(pending with{
            NextMetadataCheckUtc = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);

        test.Steam.SetResponse(102, [], new SteamAppMetadata(AppId, "Resolved Retired Game", SteamAppKind.Game, 102){
            IsRetired = true,
        });

        Assert.True(await test.RunAsync());

        var resolved = await test.ReadAppAsync();

        Assert.Equal(TrackingStatus.Announced, resolved.Status);
        Assert.True(resolved.IsRetired);
        Assert.Null(resolved.NextMetadataCheckUtc);
        Assert.Equal(test.NewNotifier.LastPostedMessageId, resolved.DiscordMessageId);
        Assert.Equal(pending.RetirementDiscordMessageId, resolved.RetirementDiscordMessageId);
        Assert.Single(test.NewNotifier.Posted);
        Assert.Single(test.RetiredNotifier.Posted);
        Assert.Single(test.RetiredNotifier.Updated);
    }

    private static TrackedSteamApp CreateTrackedApp(
        TrackingStatus status,
        bool isRetired = false,
        ulong? newMessageId = null,
        ulong? retirementMessageId = null,
        uint lastChange = 100) => new(){
        AppId = AppId,
        Name = "Stored Game",
        Kind = SteamAppKind.Game,
        Status = status,
        FirstSeenChange = 100,
        LastSeenChange = lastChange,
        DiscordMessageId = newMessageId,
        FirstSeenUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
        IsRetired = isRetired,
        RetirementDiscordMessageId = retirementMessageId,
    };

    private sealed class TestContext : IDisposable{
        private readonly string _directory;
        private readonly TestHostEnvironment _environment;

        private TestContext(){
            _directory = Path.Combine(Path.GetTempPath(), $"PhantomBotWorkerTests-{Guid.NewGuid():N}");

            Directory.CreateDirectory(_directory);

            _environment = new TestHostEnvironment(_directory);

            Options = Microsoft.Extensions.Options.Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                RemovedAppsDiscordChannelId = 2,
                DatabasePath = Path.Combine(_directory, "phantombot.db"),
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });

            Store = new SqliteTrackedAppStore(Options, _environment);
        }

        public IOptions<PhantomBotOptions> Options{ get; }
        public SqliteTrackedAppStore Store{ get; }
        public FakeSteamCatalogClient Steam{ get; } = new();
        public FakeNotifier NewNotifier{ get; } = new(1_000);
        public FakeNotifier RetiredNotifier{ get; } = new(2_000);

        public static async Task<TestContext> CreateAsync(){
            var test = new TestContext();

            try{
                await test.Store.InitializeAsync(CancellationToken.None);
                await test.Store.SetLastChangeNumberAsync(100, CancellationToken.None);

                return test;
            }
            catch{
                test.Dispose();

                throw;
            }
        }

        public async Task<bool> RunAsync(){
            using var service = new NewAppMonitorService(
                Steam,
                Store,
                NewNotifier,
                RetiredNotifier,
                new SteamBaselineCoordinator(Options, _environment, NullLogger<SteamBaselineCoordinator>.Instance),
                Options,
                new TestApplicationLifetime(),
                NullLogger<NewAppMonitorService>.Instance);

            return await service.RunPollingCycleAsync(CancellationToken.None);
        }

        public async Task<TrackedSteamApp> ReadAppAsync(){
            var apps = await Store.GetAppsAsync([AppId], CancellationToken.None);

            return apps[AppId];
        }

        public void Dispose(){
            SqliteConnection.ClearAllPools();

            try{
                if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
                // Cleanup must not mask the test result.
            }
        }
    }

    private sealed class FakeSteamCatalogClient : ISteamCatalogClient{
        private SteamChangeSet _changes = new(100, false, new Dictionary<uint, uint>());
        private Dictionary<uint, SteamAppMetadata> _metadata = [];
        public bool RequiresFullAppUpdate{ get; set; }
        public IReadOnlyDictionary<uint, SteamAppMetadata>? EnrichedMetadata{ get; set; }
        public List<uint> RequestedChangeNumbers{ get; } = [];
        public List<uint[]> PicsRequests{ get; } = [];
        public List<uint[]> EnrichedRequests{ get; } = [];

        public void SetResponse(uint currentChangeNumber, IEnumerable<uint> changedIds, params SteamAppMetadata[] metadata){
            _changes = new SteamChangeSet(currentChangeNumber, false, changedIds.ToDictionary(static id => id, _ => currentChangeNumber));

            _metadata = metadata.ToDictionary(static app => app.AppId);
            EnrichedMetadata = null;
            RequiresFullAppUpdate = false;
        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<uint> GetCurrentChangeNumberAsync(CancellationToken cancellationToken) => Task.FromResult(_changes.CurrentChangeNumber);

        public Task<SteamChangeSet> GetChangesSinceAsync(uint lastProcessedChangeNumber, CancellationToken cancellationToken){
            RequestedChangeNumbers.Add(lastProcessedChangeNumber);

            return Task.FromResult(_changes with{
                RequiresFullAppUpdate = RequiresFullAppUpdate,
            });
        }

        public Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetPicsAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
            PicsRequests.Add([.. appIds]);

            return Task.FromResult<IReadOnlyDictionary<uint, SteamAppMetadata>>(SelectMetadata(appIds, _metadata));
        }

        public Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
            EnrichedRequests.Add([.. appIds]);

            return Task.FromResult<IReadOnlyDictionary<uint, SteamAppMetadata>>(SelectMetadata(appIds, EnrichedMetadata ?? _metadata));
        }

        private static Dictionary<uint, SteamAppMetadata> SelectMetadata(IEnumerable<uint> appIds, IReadOnlyDictionary<uint, SteamAppMetadata> source){
            var result = new Dictionary<uint, SteamAppMetadata>();

            foreach (var appId in appIds){
                if (source.TryGetValue(appId, out var app)) result.Add(appId, app);
            }

            return result;
        }
    }

    private sealed class FakeNotifier(ulong startingMessageId) : INewAppNotifier, IRetiredAppNotifier{
        private ulong _nextMessageId = startingMessageId;

        public List<SteamAppMetadata> Posted{ get; } = [];
        public List<(ulong MessageId, SteamAppMetadata App)> Updated{ get; } = [];
        public ulong? LastPostedMessageId{ get; private set; }
        public ulong? UpdateResult{ get; set; }

        public Task<ulong> PostAsync(SteamAppMetadata app, CancellationToken cancellationToken){
            Posted.Add(app);

            var messageId = _nextMessageId++;

            LastPostedMessageId = messageId;

            return Task.FromResult(messageId);
        }

        public Task<ulong> UpdateAsync(ulong messageId, SteamAppMetadata app, CancellationToken cancellationToken){
            Updated.Add((messageId, app));

            return Task.FromResult(UpdateResult ?? messageId);
        }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime{
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication(){
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment{
        public string EnvironmentName{ get; set; } = Environments.Development;
        public string ApplicationName{ get; set; } = "PhantomBot.Worker.Tests";
        public string ContentRootPath{ get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider{ get; set; } = new NullFileProvider();
    }
}