using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;
using PhantomBot.Infrastructure.Persistence;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SqliteTrackedAppStoreTests{
    [Fact]
    public async Task SeedPreservesKnownMetadataAndWatchesIncompleteApps(){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            await store.SeedAppsAsync(
                [
                    new SteamAppListEntry(10, null),
                    new SteamAppListEntry(20, "Known Game", "game"),
                    new SteamAppListEntry(30, "Named but untyped"),
                    new SteamAppListEntry(40, "Known Tool", "tool"),
                ],
                100,
                CancellationToken.None);

            var apps = await store.GetAppsAsync([10, 20, 30, 40], CancellationToken.None);

            Assert.Equal(TrackingStatus.SeededIncomplete, apps[10].Status);
            Assert.NotNull(apps[10].NextMetadataCheckUtc);

            Assert.Equal(TrackingStatus.Seeded, apps[20].Status);
            Assert.Equal(SteamAppKind.Game, apps[20].Kind);
            Assert.Null(apps[20].NextMetadataCheckUtc);

            Assert.Equal(TrackingStatus.SeededIncomplete, apps[30].Status);
            Assert.NotNull(apps[30].NextMetadataCheckUtc);

            Assert.Equal(TrackingStatus.Seeded, apps[40].Status);
            Assert.Equal(SteamAppKind.Other, apps[40].Kind);
            Assert.Null(apps[40].NextMetadataCheckUtc);

            var pending = await store.GetPendingMetadataAsync(DateTimeOffset.MaxValue, 10, CancellationToken.None);

            Assert.Equal([10U, 30U], pending.Select(static app => app.AppId).Order().ToArray());
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeIsIdempotentAndPreservesCheckpoint(){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            await store.SetLastChangeNumberAsync(300, CancellationToken.None);

            await store.InitializeAsync(CancellationToken.None);

            Assert.Equal(300U, await store.GetLastChangeNumberAsync(CancellationToken.None));
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertRoundTripsTrackedApp(){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            var firstSeenUtc = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.FromHours(2));

            var updatedUtc = firstSeenUtc.AddMinutes(5);

            var expected = new TrackedSteamApp{
                AppId = 570,
                Name = "Dota 2",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.Announced,
                FirstSeenChange = 100,
                LastSeenChange = 125,
                DiscordMessageId = 123_456_789,
                FirstSeenUtc = firstSeenUtc,
                UpdatedUtc = updatedUtc,
                NextMetadataCheckUtc = null,
            };

            await store.UpsertAppAsync(expected, CancellationToken.None);

            var apps = await store.GetAppsAsync([expected.AppId], CancellationToken.None);

            var actual = apps[expected.AppId];

            Assert.Equal(expected, actual);
            Assert.Equal(firstSeenUtc.Offset, actual.FirstSeenUtc.Offset);
            Assert.Equal(updatedUtc.Offset, actual.UpdatedUtc.Offset);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertRejectsAnnouncedAppWithoutMessageId(){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;

            var app = new TrackedSteamApp{
                AppId = 570,
                Name = "Dota 2",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.Announced,
                FirstSeenChange = 100,
                LastSeenChange = 100,
                FirstSeenUtc = now,
                UpdatedUtc = now,
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAppAsync(app, CancellationToken.None));

            Assert.Contains("Announced apps require a Discord message ID.", exception.Message, StringComparison.Ordinal);
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    private static SqliteTrackedAppStore CreateStore(string databasePath){
        var contentRootPath = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Test database path must have a parent directory.");

        return new SqliteTrackedAppStore(
            Options.Create(new PhantomBotOptions{
                DatabasePath = databasePath,
            }),
            new TestHostEnvironment(contentRootPath));
    }

    private static string CreateDatabasePath(){
        var directory = Path.Combine(Path.GetTempPath(), $"PhantomBotTests-{Guid.NewGuid():N}");

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
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment{
        public string EnvironmentName{ get; set; } = Environments.Development;

        public string ApplicationName{ get; set; } = "PhantomBot.Tests";

        public string ContentRootPath{ get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider{ get; set; } = new NullFileProvider();
    }
}