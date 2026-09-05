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
    [Fact]
    public async Task RestartDoesNotPostTrackedAppAgain(){
        const uint appId = 5_000_001;
        var databasePath = CreateDatabasePath();

        try{
            var environment = new TestHostEnvironment(Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory."));

            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });

            var store = new SqliteTrackedAppStore(options, environment);
            await store.InitializeAsync(CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var app = new SteamAppMetadata(appId, "Restart Test Game", SteamAppKind.Game, 101);

            var firstSteam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>{ [appId] = 101, }), [app]);

            var notifier = new FakeNotifier();

            var firstService = CreateService(firstSteam, store, notifier, options, environment);

            Assert.True(await firstService.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(1, notifier.PostCount);
            Assert.Equal(101U, await store.GetLastChangeNumberAsync(CancellationToken.None));

            var secondSteam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>()), []);

            var restartedService = CreateService(secondSteam, store, notifier, options, environment);

            Assert.True(await restartedService.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(1, notifier.PostCount);
            Assert.Equal(0, notifier.UpdateCount);
            Assert.Equal(100U, firstSteam.RequestedChangeNumber);
            Assert.Equal(101U, secondSteam.RequestedChangeNumber);

            var trackedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.Announced, trackedApps[appId].Status);
            Assert.NotNull(trackedApps[appId].DiscordMessageId);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PendingAppIsPostedWhenMetadataRetrySucceeds(){
        const uint appId = 5_000_002;
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");

            var environment = new TestHostEnvironment(contentRootPath);

            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });

            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var notifier = new FakeNotifier();

            var missingMetadataSteam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>{ [appId] = 101, }), []);

            var firstService = CreateService(missingMetadataSteam, store, notifier, options, environment);

            Assert.True(await firstService.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(0, notifier.PostCount);

            var pendingApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.PendingMetadata, pendingApps[appId].Status);
            Assert.Null(pendingApps[appId].DiscordMessageId);

            await store.UpsertAppAsync(pendingApps[appId] with{ NextMetadataCheckUtc = DateTimeOffset.UnixEpoch, }, CancellationToken.None);

            var resolvedApp = new SteamAppMetadata(appId, "Resolved Test Game", SteamAppKind.Game, 101);

            var resolvedMetadataSteam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>()), [resolvedApp]);

            var restartedService = CreateService(resolvedMetadataSteam, store, notifier, options, environment);

            Assert.True(await restartedService.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(1, notifier.PostCount);

            var resolvedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.Announced, resolvedApps[appId].Status);
            Assert.NotNull(resolvedApps[appId].DiscordMessageId);
            Assert.Equal(101U, await store.GetLastChangeNumberAsync(CancellationToken.None));
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ContinuityLossDoesNotAdvanceCheckpoint(){
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");

            var environment = new TestHostEnvironment(contentRootPath);

            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });

            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var steam = new FakeSteamCatalogClient(new SteamChangeSet(500, true, new Dictionary<uint, uint>()), []);

            var notifier = new FakeNotifier();

            var service = CreateService(steam, store, notifier, options, environment);

            Assert.False(await service.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(100U, steam.RequestedChangeNumber);
            Assert.Equal(0, notifier.PostCount);
            Assert.Equal(0, notifier.UpdateCount);
            Assert.Equal(100U, await store.GetLastChangeNumberAsync(CancellationToken.None));
            Assert.Equal(0, await store.CountAppsAsync(CancellationToken.None));
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task OtherAppIsIgnoredWithoutNotification(){
        const uint appId = 5_000_003;
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");

            var environment = new TestHostEnvironment(contentRootPath);

            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });

            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var app = new SteamAppMetadata(appId, "Test Tool", SteamAppKind.Other, 101);

            var steam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>{ [appId] = 101, }), [app]);

            var notifier = new FakeNotifier();

            var service = CreateService(steam, store, notifier, options, environment);

            Assert.True(await service.RunPollingCycleAsync(CancellationToken.None));

            Assert.Equal(0, notifier.PostCount);
            Assert.Equal(0, notifier.UpdateCount);

            var trackedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.Ignored, trackedApps[appId].Status);
            Assert.Equal(SteamAppKind.Other, trackedApps[appId].Kind);
            Assert.Null(trackedApps[appId].NextMetadataCheckUtc);
            Assert.Equal(101U, await store.GetLastChangeNumberAsync(CancellationToken.None));
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ChangedSeededIncompleteAppResolvesWithoutHistoricalNotification(){
        const uint appId = 5_000_004;
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");
            var environment = new TestHostEnvironment(contentRootPath);
            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });
            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SeedAppsAsync([new SteamAppListEntry(appId, null)], 100, CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var metadata = new SteamAppMetadata(appId, "Historical Game", SteamAppKind.Game, 101);
            var steam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>{ [appId] = 101, }), [metadata]);
            var notifier = new FakeNotifier();
            var service = CreateService(steam, store, notifier, options, environment);

            Assert.True(await service.RunPollingCycleAsync(CancellationToken.None));

            var trackedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.Seeded, trackedApps[appId].Status);
            Assert.Equal(101U, trackedApps[appId].LastSeenChange);
            Assert.Null(trackedApps[appId].DiscordMessageId);
            Assert.Null(trackedApps[appId].NextMetadataCheckUtc);
            Assert.Equal(0, notifier.PostCount);
            Assert.Equal(0, notifier.UpdateCount);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task MissingMetadataPreservesSeededIncompleteOrigin(){
        const uint appId = 5_000_006;
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");
            var environment = new TestHostEnvironment(contentRootPath);
            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });
            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SeedAppsAsync([new SteamAppListEntry(appId, null)], 100, CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var steam = new FakeSteamCatalogClient(new SteamChangeSet(100, false, new Dictionary<uint, uint>()), []);
            var notifier = new FakeNotifier();
            var service = CreateService(steam, store, notifier, options, environment);

            Assert.True(await service.RunPollingCycleAsync(CancellationToken.None));

            var trackedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(TrackingStatus.SeededIncomplete, trackedApps[appId].Status);
            Assert.Null(trackedApps[appId].DiscordMessageId);
            Assert.NotNull(trackedApps[appId].NextMetadataCheckUtc);
            Assert.Equal(0, notifier.PostCount);
            Assert.Equal(0, notifier.UpdateCount);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplacementMessageIdIsPersisted(){
        const uint appId = 5_000_005;
        const ulong originalMessageId = 10;
        const ulong replacementMessageId = 20;
        var databasePath = CreateDatabasePath();

        try{
            var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");
            var environment = new TestHostEnvironment(contentRootPath);
            var options = Options.Create(new PhantomBotOptions{
                NewAppDiscordChannelId = 1,
                DatabasePath = databasePath,
                PollIntervalSeconds = 15,
                MetadataRetrySeconds = 60,
                PostUnknownApps = false,
            });
            var store = new SqliteTrackedAppStore(options, environment);

            await store.InitializeAsync(CancellationToken.None);
            await store.SetLastChangeNumberAsync(100, CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            await store.UpsertAppAsync(new TrackedSteamApp{
                AppId = appId,
                Name = "Tracked Game",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.Announced,
                FirstSeenChange = 100,
                LastSeenChange = 100,
                DiscordMessageId = originalMessageId,
                FirstSeenUtc = now,
                UpdatedUtc = now,
            }, CancellationToken.None);

            var metadata = new SteamAppMetadata(appId, "Updated Game", SteamAppKind.Game, 101);
            var steam = new FakeSteamCatalogClient(new SteamChangeSet(101, false, new Dictionary<uint, uint>{ [appId] = 101, }), [metadata]);
            var notifier = new FakeNotifier{ UpdateResult = replacementMessageId };
            var service = CreateService(steam, store, notifier, options, environment);

            Assert.True(await service.RunPollingCycleAsync(CancellationToken.None));

            var trackedApps = await store.GetAppsAsync([appId], CancellationToken.None);

            Assert.Equal(replacementMessageId, trackedApps[appId].DiscordMessageId);
            Assert.Equal(0, notifier.PostCount);
            Assert.Equal(1, notifier.UpdateCount);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    private static NewAppMonitorService CreateService(ISteamCatalogClient steam, ITrackedAppStore store, INewAppNotifier notifier, IOptions<PhantomBotOptions> options, IHostEnvironment environment) =>
        new(steam, store, notifier, new SteamBaselineCoordinator(options, environment, NullLogger<SteamBaselineCoordinator>.Instance), options, new TestApplicationLifetime(), NullLogger<NewAppMonitorService>.Instance);

    private static string CreateDatabasePath(){
        var directory = Path.Combine(Path.GetTempPath(), $"PhantomBotWorkerTests-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "phantombot.db");
    }

    private static void DeleteDatabase(string databasePath){
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(databasePath);

        if (directory is null || !Directory.Exists(directory)) return;

        try{
            Directory.Delete(directory, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
            // Cleanup must not mask the test result.
        }
    }

    private sealed class FakeSteamCatalogClient : ISteamCatalogClient{
        private readonly SteamChangeSet _changes;

        private readonly Dictionary<uint, SteamAppMetadata> _metadata;

        public FakeSteamCatalogClient(SteamChangeSet changes, IEnumerable<SteamAppMetadata> metadata){
            _changes = changes;
            _metadata = metadata.ToDictionary(static app => app.AppId);
        }

        public uint? RequestedChangeNumber{ get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<uint> GetCurrentChangeNumberAsync(CancellationToken cancellationToken) => Task.FromResult(_changes.CurrentChangeNumber);

        public Task<SteamChangeSet> GetChangesSinceAsync(uint lastProcessedChangeNumber, CancellationToken cancellationToken){
            RequestedChangeNumber = lastProcessedChangeNumber;
            return Task.FromResult(_changes);
        }

        public Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
            var result = new Dictionary<uint, SteamAppMetadata>();

            foreach (var requestedAppId in appIds){
                if (_metadata.TryGetValue(requestedAppId, out var metadata)) result.Add(requestedAppId, metadata);
            }

            return Task.FromResult<IReadOnlyDictionary<uint, SteamAppMetadata>>(result);
        }
    }

    private sealed class FakeNotifier : INewAppNotifier{
        public int PostCount{ get; private set; }

        public int UpdateCount{ get; private set; }

        public ulong? UpdateResult{ get; init; }

        public Task<ulong> PostAsync(SteamAppMetadata app, CancellationToken cancellationToken){
            PostCount++;
            return Task.FromResult((ulong)PostCount);
        }

        public Task<ulong> UpdateAsync(ulong messageId, SteamAppMetadata app, CancellationToken cancellationToken){
            UpdateCount++;
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
