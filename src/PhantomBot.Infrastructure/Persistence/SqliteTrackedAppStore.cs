using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Persistence;

public sealed class SqliteTrackedAppStore(IOptions<PhantomBotOptions> options, IHostEnvironment environment) : ITrackedAppStore{
    private const int AppQueryBatchSize = 500;

    private readonly string _connectionString = CreateConnectionString(options.Value.DatabasePath, environment.ContentRootPath);

    public async Task InitializeAsync(CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
                              PRAGMA journal_mode = WAL;

                              CREATE TABLE IF NOT EXISTS bot_state (
                                  key TEXT PRIMARY KEY,
                                  value TEXT NOT NULL
                              );

                              CREATE TABLE IF NOT EXISTS steam_apps (
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

                              CREATE INDEX IF NOT EXISTS ix_steam_apps_pending
                              ON steam_apps(status, next_metadata_check_utc);
                              """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> CountAppsAsync(CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM steam_apps;";

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task SeedAppsAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint currentChangeNumber, CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
                              INSERT OR IGNORE INTO steam_apps (
                                  app_id,
                                  name,
                                  kind,
                                  status,
                                  first_seen_change,
                                  last_seen_change,
                                  discord_message_id,
                                  first_seen_utc,
                                  updated_utc,
                                  next_metadata_check_utc
                              ) VALUES (
                                  $appId,
                                  $name,
                                  $kind,
                                  $status,
                                  $change,
                                  $change,
                                  NULL,
                                  $now,
                                  $now,
                                  $nextCheck
                              );
                              """;

        var appId = command.Parameters.Add("$appId", SqliteType.Integer);

        var name = command.Parameters.Add("$name", SqliteType.Text);

        var kind = command.Parameters.Add("$kind", SqliteType.Integer);

        var status = command.Parameters.Add("$status", SqliteType.Integer);

        var nextCheck = command.Parameters.Add("$nextCheck", SqliteType.Text);

        command.Parameters.AddWithValue("$change", (long)currentChangeNumber);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        command.Parameters.AddWithValue("$now", now);

        foreach (var app in apps){
            cancellationToken.ThrowIfCancellationRequested();

            if (app.AppId == 0) throw new ArgumentException("A Steam baseline cannot contain AppID 0.", nameof(apps));

            var appKind = SteamAppClassifier.Classify(app.RawType);

            var isResolved = appKind == SteamAppKind.Other || (appKind.IsWanted() && !string.IsNullOrWhiteSpace(app.Name));

            appId.Value = (long)app.AppId;

            name.Value = string.IsNullOrWhiteSpace(app.Name) ? DBNull.Value : app.Name;

            kind.Value = (int)appKind;

            status.Value = isResolved ? (int)TrackingStatus.Seeded : (int)TrackingStatus.SeededIncomplete;

            nextCheck.Value = isResolved ? DBNull.Value : now;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<uint, TrackedSteamApp>> GetAppsAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
        if (appIds.Count == 0) return new Dictionary<uint, TrackedSteamApp>();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var result = new Dictionary<uint, TrackedSteamApp>(appIds.Count);

        foreach (var batch in appIds.Distinct().Chunk(AppQueryBatchSize)){
            await using var command = connection.CreateCommand();

            var parameterNames = new string[batch.Length];

            for (var index = 0; index < batch.Length; index++){
                var parameterName = $"$app{index}";

                parameterNames[index] = parameterName;

                command.Parameters.AddWithValue(parameterName, (long)batch[index]);
            }

            command.CommandText = $"SELECT {Columns} FROM steam_apps WHERE app_id IN ({string.Join(',', parameterNames)});";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken)){
                var app = ReadApp(reader);
                result.Add(app.AppId, app);
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<TrackedSteamApp>> GetPendingMetadataAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = $"""
                               SELECT {Columns}
                               FROM steam_apps
                               WHERE status IN (
                                   $pending,
                                   $seededIncomplete
                               )
                                 AND next_metadata_check_utc IS NOT NULL
                                 AND next_metadata_check_utc <= $dueAt
                               ORDER BY
                                   next_metadata_check_utc,
                                   first_seen_utc
                               LIMIT $limit;
                               """;

        command.Parameters.AddWithValue("$pending", (int)TrackingStatus.PendingMetadata);

        command.Parameters.AddWithValue("$seededIncomplete", (int)TrackingStatus.SeededIncomplete);

        command.Parameters.AddWithValue("$dueAt", dueAt.ToString("O", CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TrackedSteamApp>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadApp(reader));

        return result;
    }

    public async Task UpsertAppAsync(TrackedSteamApp app, CancellationToken cancellationToken){
        ValidateForWrite(app);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
                              INSERT INTO steam_apps (
                                  app_id,
                                  name,
                                  kind,
                                  status,
                                  first_seen_change,
                                  last_seen_change,
                                  discord_message_id,
                                  first_seen_utc,
                                  updated_utc,
                                  next_metadata_check_utc
                              ) VALUES (
                                  $appId,
                                  $name,
                                  $kind,
                                  $status,
                                  $firstChange,
                                  $lastChange,
                                  $messageId,
                                  $firstSeen,
                                  $updated,
                                  $nextCheck
                              )
                              ON CONFLICT(app_id) DO UPDATE SET
                                  name = excluded.name,
                                  kind = excluded.kind,
                                  status = excluded.status,
                                  last_seen_change =
                                      excluded.last_seen_change,
                                  discord_message_id =
                                      excluded.discord_message_id,
                                  updated_utc =
                                      excluded.updated_utc,
                                  next_metadata_check_utc =
                                      excluded.next_metadata_check_utc;
                              """;

        command.Parameters.AddWithValue("$appId", (long)app.AppId);

        command.Parameters.AddWithValue("$name", DbValue(app.Name));

        command.Parameters.AddWithValue("$kind", (int)app.Kind);

        command.Parameters.AddWithValue("$status", (int)app.Status);

        command.Parameters.AddWithValue("$firstChange", (long)app.FirstSeenChange);

        command.Parameters.AddWithValue("$lastChange", (long)app.LastSeenChange);

        command.Parameters.AddWithValue("$messageId", app.DiscordMessageId is null ? DBNull.Value : app.DiscordMessageId.Value.ToString(CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue("$firstSeen", app.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue("$updated", app.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue("$nextCheck", app.NextMetadataCheckUtc is null ? DBNull.Value : app.NextMetadataCheckUtc.Value.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<uint?> GetLastChangeNumberAsync(CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT value FROM bot_state WHERE key = 'last_change_number';";

        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is null) return null;

        if (result is not string value || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var changeNumber)) throw new InvalidDataException("The stored Steam change-number checkpoint is invalid.");

        return changeNumber;
    }

    public async Task SetLastChangeNumberAsync(uint changeNumber, CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
                              INSERT INTO bot_state(key, value)
                              VALUES ('last_change_number', $value)
                              ON CONFLICT(key) DO UPDATE SET
                                  value = excluded.value;
                              """;

        command.Parameters.AddWithValue("$value", changeNumber.ToString(CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken){
        var connection = new SqliteConnection(_connectionString);

        try{
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA synchronous = NORMAL;";

            await command.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch{
            await connection.DisposeAsync();
            throw;
        }
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static TrackedSteamApp ReadApp(SqliteDataReader reader){
        var messageId = reader.IsDBNull(6) ? null : reader.GetString(6);

        var nextCheck = reader.IsDBNull(9) ? null : reader.GetString(9);

        var app = new TrackedSteamApp{
            AppId = checked((uint)reader.GetInt64(0)),

            Name = reader.IsDBNull(1) ? null : reader.GetString(1),

            Kind = (SteamAppKind)reader.GetInt32(2),

            Status = (TrackingStatus)reader.GetInt32(3),

            FirstSeenChange = checked((uint)reader.GetInt64(4)),

            LastSeenChange = checked((uint)reader.GetInt64(5)),

            DiscordMessageId = messageId is null ? null : ulong.Parse(messageId, CultureInfo.InvariantCulture),

            FirstSeenUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),

            UpdatedUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),

            NextMetadataCheckUtc = nextCheck is null ? null : DateTimeOffset.Parse(nextCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };

        var validationError = GetValidationError(app);

        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (validationError is not null) throw new InvalidDataException($"Stored Steam AppID {app.AppId} is invalid: " + validationError);

        return app;
    }

    private static void ValidateForWrite(TrackedSteamApp app){
        ArgumentNullException.ThrowIfNull(app);

        var validationError = GetValidationError(app);

        if (validationError is not null) throw new ArgumentException($"Tracked Steam app is invalid: {validationError}", nameof(app));
    }

    private static string? GetValidationError(TrackedSteamApp app){
        if (app.AppId == 0) return "AppID must be non-zero.";

        if (!Enum.IsDefined(app.Kind)) return $"kind value {(int)app.Kind} is unknown.";

        if (!Enum.IsDefined(app.Status)) return $"status value {(int)app.Status} is unknown.";

        if (app.LastSeenChange < app.FirstSeenChange) return "last-seen change number precedes the first-seen change number.";

        if (app.FirstSeenUtc == default) return "first-seen timestamp is missing.";

        if (app.UpdatedUtc == default) return "updated timestamp is missing.";

        if (app.DiscordMessageId == 0) return "Discord message ID must be non-zero when present.";

        return app.Status switch{
            TrackingStatus.Seeded when app.DiscordMessageId is not null => "Seeded historical apps cannot have a Discord message ID.",

            TrackingStatus.Seeded when app.NextMetadataCheckUtc is not null => "Seeded historical apps cannot have a metadata retry time.",

            TrackingStatus.SeededIncomplete when app.DiscordMessageId is not null => "Incomplete historical apps cannot have a Discord message ID.",

            TrackingStatus.SeededIncomplete when app.NextMetadataCheckUtc is null => "Incomplete historical apps require a metadata retry time.",

            TrackingStatus.PendingMetadata when app.NextMetadataCheckUtc is null => "Pending apps require a metadata retry time.",

            TrackingStatus.Announced when app.DiscordMessageId is null => "Announced apps require a Discord message ID.",

            TrackingStatus.Announced when app.NextMetadataCheckUtc is not null => "Announced apps cannot have a metadata retry time.",

            TrackingStatus.Ignored when app.NextMetadataCheckUtc is not null => "Ignored apps cannot have a metadata retry time.",

            _ => null,
        };
    }

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static string CreateConnectionString(string configuredPath, string contentRootPath){
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var fullPath = Path.IsPathRooted(configuredPath) ? Path.GetFullPath(configuredPath) : Path.GetFullPath(configuredPath, contentRootPath);

        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Database path must have a parent directory.");

        Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder{
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    private const string Columns = """
                                   app_id,
                                   name,
                                   kind,
                                   status,
                                   first_seen_change,
                                   last_seen_change,
                                   discord_message_id,
                                   first_seen_utc,
                                   updated_utc,
                                   next_metadata_check_utc
                                   """;
}