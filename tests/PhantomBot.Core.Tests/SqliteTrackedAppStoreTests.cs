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
                new SteamAppListEntry(50, "Known Application", "application"),
                new SteamAppListEntry(60, "Retired Game", "game", true),
                new SteamAppListEntry(70, "Retired Application", "application", true),
                new SteamAppListEntry(80, "Unrecognized Type", "unrecognized-type"),
                new SteamAppListEntry(90, "Retired Tool", "tool", true),
                new SteamAppListEntry(100, null, "tool"),
            ], 100, CancellationToken.None);

            var apps = await store.GetAppsAsync([10, 20, 30, 40, 50, 60, 70, 80, 90, 100], CancellationToken.None);

            Assert.Equal(10, apps.Count);
            Assert.Equal(TrackingStatus.SeededIncomplete, apps[10].Status);
            Assert.Equal(SteamAppKind.Unknown, apps[10].Kind);
            Assert.NotNull(apps[10].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[20].Status);
            Assert.Equal(SteamAppKind.Game, apps[20].Kind);
            Assert.Null(apps[20].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.SeededIncomplete, apps[30].Status);
            Assert.Equal(SteamAppKind.Unknown, apps[30].Kind);
            Assert.NotNull(apps[30].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[40].Status);
            Assert.Equal(SteamAppKind.Tool, apps[40].Kind);
            Assert.Null(apps[40].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[50].Status);
            Assert.Equal(SteamAppKind.Application, apps[50].Kind);
            Assert.Null(apps[50].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[60].Status);
            Assert.Equal(SteamAppKind.Game, apps[60].Kind);
            Assert.True(apps[60].IsRetired);
            Assert.Null(apps[60].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[70].Status);
            Assert.Equal(SteamAppKind.Application, apps[70].Kind);
            Assert.True(apps[70].IsRetired);
            Assert.Null(apps[70].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[80].Status);
            Assert.Equal(SteamAppKind.Other, apps[80].Kind);
            Assert.Null(apps[80].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.Seeded, apps[90].Status);
            Assert.Equal(SteamAppKind.Tool, apps[90].Kind);
            Assert.True(apps[90].IsRetired);
            Assert.Null(apps[90].NextMetadataCheckUtc);
            Assert.Equal(TrackingStatus.SeededIncomplete, apps[100].Status);
            Assert.Equal(SteamAppKind.Tool, apps[100].Kind);
            Assert.Null(apps[100].Name);
            Assert.NotNull(apps[100].NextMetadataCheckUtc);
            Assert.False(apps[10].IsRetired);
            Assert.False(apps[20].IsRetired);
            Assert.False(apps[30].IsRetired);
            Assert.False(apps[40].IsRetired);
            Assert.False(apps[50].IsRetired);
            Assert.False(apps[80].IsRetired);
            Assert.False(apps[100].IsRetired);
            Assert.All(apps.Values, static app => {
                Assert.Null(app.DiscordMessageId);
                Assert.Null(app.RetirementDiscordMessageId);
                Assert.Equal(100U, app.FirstSeenChange);
                Assert.Equal(100U, app.LastSeenChange);
            });

            var pending = await store.GetPendingMetadataAsync(DateTimeOffset.MaxValue, 10, CancellationToken.None);

            Assert.Equal([10U, 30U, 100U], pending.Select(static app => app.AppId).Order().ToArray());
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

    [Theory]
    [InlineData(SteamAppKind.Game, false)]
    [InlineData(SteamAppKind.Game, true)]
    [InlineData(SteamAppKind.Tool, false)]
    [InlineData(SteamAppKind.Tool, true)]
    public async Task UpsertRoundTripsTrackedApp(SteamAppKind kind, bool isRetired){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            var firstSeenUtc = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.FromHours(2));
            var updatedUtc = firstSeenUtc.AddMinutes(5);

            var expected = new TrackedSteamApp{
                AppId = 570,
                Name = "Test App",
                Kind = kind,
                Status = TrackingStatus.Announced,
                FirstSeenChange = 100,
                LastSeenChange = 125,
                DiscordMessageId = 123_456_789,
                FirstSeenUtc = firstSeenUtc,
                UpdatedUtc = updatedUtc,
                NextMetadataCheckUtc = null,
                IsRetired = isRetired,
                RetirementDiscordMessageId = 987_654_321,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeMigratesLegacyDatabaseAndPreservesData(bool hasRetiredColumn){
        var databasePath = CreateDatabasePath();

        try{
            await CreateLegacyDatabaseAsync(databasePath, hasRetiredColumn);

            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            var firstSeenUtc = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

            var expected = new TrackedSteamApp{
                AppId = 570,
                Name = "Test Game",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.PendingMetadata,
                FirstSeenChange = 100,
                LastSeenChange = 125,
                DiscordMessageId = 123_456_789,
                FirstSeenUtc = firstSeenUtc,
                UpdatedUtc = firstSeenUtc.AddMinutes(1),
                NextMetadataCheckUtc = firstSeenUtc.AddMinutes(5),
                IsRetired = hasRetiredColumn,
                RetirementDiscordMessageId = null,
            };

            var apps = await store.GetAppsAsync([570], CancellationToken.None);

            Assert.Equal(expected, apps[570]);
            Assert.Equal(1L, await store.CountAppsAsync(CancellationToken.None));
            Assert.Equal(300U, await store.GetLastChangeNumberAsync(CancellationToken.None));

            var pending = await store.GetPendingMetadataAsync(firstSeenUtc.AddMinutes(6), 10, CancellationToken.None);

            Assert.Equal(expected, Assert.Single(pending));

            var updated = expected with{
                IsRetired = true,
                RetirementDiscordMessageId = 987_654_321,
                LastSeenChange = 130,
                UpdatedUtc = firstSeenUtc.AddMinutes(2),
            };

            await store.UpsertAppAsync(updated, CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            apps = await store.GetAppsAsync([570], CancellationToken.None);

            Assert.Equal(updated, apps[570]);
            Assert.Equal(1L, await store.CountAppsAsync(CancellationToken.None));
            Assert.Equal(300U, await store.GetLastChangeNumberAsync(CancellationToken.None));
        }
        finally{
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertUpdatesRetirementAndPreservesNotificationIdAfterReversal(){
        var databasePath = CreateDatabasePath();

        try{
            var store = CreateStore(databasePath);

            await store.InitializeAsync(CancellationToken.None);

            var now = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

            var original = new TrackedSteamApp{
                AppId = 570,
                Name = "Test Game",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.Seeded,
                FirstSeenChange = 100,
                LastSeenChange = 100,
                FirstSeenUtc = now,
                UpdatedUtc = now,
                IsRetired = false,
            };

            await store.UpsertAppAsync(original, CancellationToken.None);

            var retired = original with{
                LastSeenChange = 110,
                UpdatedUtc = now.AddMinutes(1),
                IsRetired = true,
                RetirementDiscordMessageId = 987_654_321,
            };

            await store.UpsertAppAsync(retired, CancellationToken.None);

            var apps = await store.GetAppsAsync([original.AppId], CancellationToken.None);

            Assert.Equal(retired, apps[original.AppId]);

            var replacement = retired with{
                LastSeenChange = 120,
                UpdatedUtc = now.AddMinutes(2),
                RetirementDiscordMessageId = 987_654_322,
            };

            await store.UpsertAppAsync(replacement, CancellationToken.None);

            apps = await store.GetAppsAsync([original.AppId], CancellationToken.None);

            Assert.Equal(replacement, apps[original.AppId]);

            var restored = replacement with{
                LastSeenChange = 130,
                UpdatedUtc = now.AddMinutes(3),
                IsRetired = false,
            };

            await store.UpsertAppAsync(restored, CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            apps = await store.GetAppsAsync([original.AppId], CancellationToken.None);

            Assert.Equal(restored, apps[original.AppId]);
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

            var now = new DateTimeOffset(2026, 9, 4, 10, 30, 0, TimeSpan.Zero);

            var app = new TrackedSteamApp{
                AppId = 570,
                Name = "Dota 2",
                Kind = SteamAppKind.Game,
                Status = TrackingStatus.Announced,
                FirstSeenChange = 100,
                LastSeenChange = 100,
                FirstSeenUtc = now,
                UpdatedUtc = now,
                IsRetired = false,
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
            // Cleanup must not hide an assertion failure.
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment{
        public string EnvironmentName{ get; set; } = Environments.Development;
        public string ApplicationName{ get; set; } = "PhantomBot.Tests";
        public string ContentRootPath{ get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider{ get; set; } = new NullFileProvider();
    }

    private static async Task CreateLegacyDatabaseAsync(string databasePath, bool hasRetiredColumn){
        var connectionString = new SqliteConnectionStringBuilder{
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              CREATE TABLE bot_state (
                                  key TEXT PRIMARY KEY,
                                  value TEXT NOT NULL
                              );

                              CREATE TABLE steam_apps (
                                  app_id INTEGER PRIMARY KEY,
                                  name TEXT NULL,
                                  kind INTEGER NOT NULL,
                                  status INTEGER NOT NULL,
                                  first_seen_change INTEGER NOT NULL,
                                  last_seen_change INTEGER NOT NULL,
                                  discord_message_id TEXT NULL,
                                  first_seen_utc TEXT NOT NULL,
                                  updated_utc TEXT NOT NULL,
                                  next_metadata_check_utc TEXT NULL
                              );

                              CREATE INDEX ix_steam_apps_pending
                              ON steam_apps(status, next_metadata_check_utc);

                              INSERT INTO bot_state(key, value)
                              VALUES ('last_change_number', '300');

                              INSERT INTO steam_apps (
                                  app_id, name, kind, status,
                                  first_seen_change, last_seen_change,
                                  discord_message_id, first_seen_utc,
                                  updated_utc, next_metadata_check_utc
                              ) VALUES (
                                  570, 'Test Game', 1, 1,
                                  100, 125,
                                  '123456789',
                                  '2026-09-04T10:30:00.0000000+00:00',
                                  '2026-09-04T10:31:00.0000000+00:00',
                                  '2026-09-04T10:35:00.0000000+00:00'
                              );
                              """;

        await command.ExecuteNonQueryAsync(CancellationToken.None);

        if (!hasRetiredColumn) return;

        command.CommandText = """
                              ALTER TABLE steam_apps
                              ADD COLUMN is_retired INTEGER NOT NULL DEFAULT 0
                                  CHECK (is_retired IN (0, 1));

                              UPDATE steam_apps
                              SET is_retired = 1
                              WHERE app_id = 570;
                              """;

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}