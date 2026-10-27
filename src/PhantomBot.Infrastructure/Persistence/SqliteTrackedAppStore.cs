using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Persistence;

public sealed class SqliteTrackedAppStore(IOptions<PhantomBotOptions> options, IHostEnvironment environment) : ITrackedAppStore{
    private const int AppQueryBatchSize = 500;
    private readonly string _connectionString = SqliteStoreConnection.CreateConnectionString(options.Value.DatabasePath, environment.ContentRootPath);

    public async Task InitializeAsync(CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using (var journalCommand = connection.CreateCommand()){
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";

            await journalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = """
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
                                  next_metadata_check_utc TEXT NULL,
                                  is_retired INTEGER NOT NULL DEFAULT 0
                                      CHECK (is_retired IN (0, 1)),
                                  retirement_discord_message_id TEXT NULL,
                                  retirement_observed_change INTEGER NULL
                                      CHECK (retirement_observed_change BETWEEN 0 AND 4294967295)
                              );

                              CREATE INDEX IF NOT EXISTS ix_steam_apps_pending
                              ON steam_apps(status, next_metadata_check_utc);
                              """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureRetirementColumnsAsync(connection, transaction, cancellationToken);

        command.CommandText = """
                              CREATE INDEX IF NOT EXISTS ix_steam_apps_retirement_unobserved
                              ON steam_apps(app_id)
                              WHERE retirement_observed_change IS NULL;
                              """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> CountAppsAsync(CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM steam_apps;";

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task SeedAppsAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint currentChangeNumber, CancellationToken cancellationToken){
        ArgumentNullException.ThrowIfNull(apps);

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
                                  next_metadata_check_utc,
                                  is_retired,
                                  retirement_discord_message_id,
                                  retirement_observed_change
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
                                  $nextCheck,
                                  $isRetired,
                                  NULL,
                                  NULL
                              );
                              """;

        var appId = command.Parameters.Add("$appId", SqliteType.Integer);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var status = command.Parameters.Add("$status", SqliteType.Integer);
        var nextCheck = command.Parameters.Add("$nextCheck", SqliteType.Text);
        var isRetired = command.Parameters.Add("$isRetired", SqliteType.Integer);

        command.Parameters.AddWithValue("$change", (long)currentChangeNumber);

        var now = FormatTimestamp(DateTimeOffset.UtcNow);

        command.Parameters.AddWithValue("$now", now);

        foreach (var app in apps){
            cancellationToken.ThrowIfCancellationRequested();

            if (app.AppId == 0) throw new ArgumentException("A Steam baseline cannot contain AppID 0.", nameof(apps));

            var appKind = SteamAppClassifier.Classify(app.RawType);
            var isResolved = (appKind is SteamAppKind.Other or SteamAppKind.Application) || (appKind.IsWanted() && !string.IsNullOrWhiteSpace(app.Name));

            appId.Value = (long)app.AppId;
            name.Value = string.IsNullOrWhiteSpace(app.Name) ? DBNull.Value : app.Name;
            kind.Value = (int)appKind;
            status.Value = isResolved ? (int)TrackingStatus.Seeded : (int)TrackingStatus.SeededIncomplete;
            nextCheck.Value = isResolved ? DBNull.Value : now;
            isRetired.Value = app.IsRetired ? 1 : 0;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> MergeRecoveryBaselineAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint baselineChangeNumber, CancellationToken cancellationToken){
        ArgumentNullException.ThrowIfNull(apps);
        cancellationToken.ThrowIfCancellationRequested();

        if (apps.Count == 0) return 0;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

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
                                  next_metadata_check_utc,
                                  is_retired,
                                  retirement_discord_message_id,
                                  retirement_observed_change
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
                                  NULL,
                                  $isRetired,
                                  NULL,
                                  NULL
                              )
                              ON CONFLICT(app_id) DO NOTHING;
                              """;

        var appIdParameter = command.Parameters.Add("$appId", SqliteType.Integer);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        var kindParameter = command.Parameters.Add("$kind", SqliteType.Integer);
        var retiredParameter = command.Parameters.Add("$isRetired", SqliteType.Integer);

        command.Parameters.AddWithValue("$status", (int)TrackingStatus.Seeded);
        command.Parameters.AddWithValue("$change", (long)baselineChangeNumber);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));

        var insertedCount = 0;

        foreach (var app in apps){
            cancellationToken.ThrowIfCancellationRequested();

            if (app is null || app.AppId == 0) throw new ArgumentException("A recovery baseline requires non-null entries with non-zero AppIDs.", nameof(apps));

            appIdParameter.Value = (long)app.AppId;
            nameParameter.Value = string.IsNullOrWhiteSpace(app.Name) ? DBNull.Value : app.Name;
            kindParameter.Value = (int)SteamAppClassifier.Classify(app.RawType);
            retiredParameter.Value = app.IsRetired ? 1 : 0;

            insertedCount += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return insertedCount;
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
        command.Parameters.AddWithValue("$dueAt", FormatTimestamp(dueAt));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TrackedSteamApp>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadApp(reader));

        return result;
    }

    public async Task<IReadOnlyList<uint>> GetPendingRetirementAppIdsAsync(uint afterAppId, int limit, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              SELECT app_id
                              FROM steam_apps
                              WHERE retirement_observed_change IS NULL
                                AND app_id > $afterAppId
                              ORDER BY app_id
                              LIMIT $limit;
                              """;

        command.Parameters.AddWithValue("$afterAppId", (long)afterAppId);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<uint>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken)) result.Add(checked((uint)reader.GetInt64(0)));

        return result;
    }

    public async Task<int> InitializeRetirementStatesAsync(IReadOnlyCollection<SteamAppMetadata> apps, CancellationToken cancellationToken){
        ArgumentNullException.ThrowIfNull(apps);

        if (apps.Count == 0) return 0;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
                              UPDATE steam_apps
                              SET is_retired = $isRetired,
                                  retirement_observed_change = $observedChange
                              WHERE app_id = $appId
                                AND retirement_observed_change IS NULL;
                              """;

        var appIdParameter = command.Parameters.Add("$appId", SqliteType.Integer);
        var isRetiredParameter = command.Parameters.Add("$isRetired", SqliteType.Integer);
        var observedChangeParameter = command.Parameters.Add("$observedChange", SqliteType.Integer);

        var seenIds = new HashSet<uint>();
        var initializedCount = 0;

        foreach (var app in apps){
            cancellationToken.ThrowIfCancellationRequested();

            if (app is null || app.AppId == 0) throw new ArgumentException("Retirement observations require non-null metadata with a non-zero AppID.", nameof(apps));

            if (!seenIds.Add(app.AppId)) throw new ArgumentException("Retirement observations cannot contain duplicate AppIDs.", nameof(apps));

            appIdParameter.Value = (long)app.AppId;
            isRetiredParameter.Value = app.IsRetired ? 1 : 0;
            observedChangeParameter.Value = (long)app.ChangeNumber;

            initializedCount += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return initializedCount;
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
                                  next_metadata_check_utc,
                                  is_retired,
                                  retirement_discord_message_id,
                                  retirement_observed_change
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
                                  $nextCheck,
                                  $isRetired,
                                  $retirementMessageId,
                                  $retirementObservedChange
                              )
                              ON CONFLICT(app_id) DO UPDATE SET
                                  name = excluded.name,
                                  kind = excluded.kind,
                                  status = excluded.status,
                                  last_seen_change = excluded.last_seen_change,
                                  discord_message_id = excluded.discord_message_id,
                                  updated_utc = excluded.updated_utc,
                                  next_metadata_check_utc = excluded.next_metadata_check_utc,
                                  is_retired = CASE
                                      WHEN steam_apps.retirement_observed_change IS NULL
                                        OR excluded.retirement_observed_change >= steam_apps.retirement_observed_change
                                      THEN excluded.is_retired
                                      ELSE steam_apps.is_retired
                                  END,
                                  retirement_observed_change = CASE
                                      WHEN steam_apps.retirement_observed_change IS NULL
                                        OR excluded.retirement_observed_change >= steam_apps.retirement_observed_change
                                      THEN excluded.retirement_observed_change
                                      ELSE steam_apps.retirement_observed_change
                                  END,
                                  retirement_discord_message_id = CASE
                                     WHEN steam_apps.retirement_observed_change IS NULL
                                         OR excluded.retirement_observed_change >= steam_apps.retirement_observed_change
                                    THEN COALESCE(
                                      excluded.retirement_discord_message_id,
                                      steam_apps.retirement_discord_message_id
                                  )
                                    ELSE steam_apps.retirement_discord_message_id
                                  END;
                              """;

        command.Parameters.AddWithValue("$appId", (long)app.AppId);
        command.Parameters.AddWithValue("$name", DbValue(app.Name));
        command.Parameters.AddWithValue("$kind", (int)app.Kind);
        command.Parameters.AddWithValue("$status", (int)app.Status);
        command.Parameters.AddWithValue("$firstChange", (long)app.FirstSeenChange);
        command.Parameters.AddWithValue("$lastChange", (long)app.LastSeenChange);
        command.Parameters.AddWithValue("$messageId", app.DiscordMessageId is null ? DBNull.Value : app.DiscordMessageId.Value.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$firstSeen", FormatTimestamp(app.FirstSeenUtc));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(app.UpdatedUtc));
        command.Parameters.AddWithValue("$nextCheck", app.NextMetadataCheckUtc is null ? DBNull.Value : FormatTimestamp(app.NextMetadataCheckUtc.Value));
        command.Parameters.AddWithValue("$isRetired", app.IsRetired ? 1 : 0);
        command.Parameters.AddWithValue("$retirementMessageId", app.RetirementDiscordMessageId is null ? DBNull.Value : app.RetirementDiscordMessageId.Value.ToString(CultureInfo.InvariantCulture));
        command.Parameters.Add("$retirementObservedChange", SqliteType.Integer).Value = app.RetirementObservedChange is{ } observedChange ? (long)observedChange : DBNull.Value;

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

    private Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) => SqliteStoreConnection.OpenAsync(_connectionString, cancellationToken);

    private static async Task EnsureRetirementColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken){
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
                              SELECT 1
                              FROM pragma_table_info('steam_apps')
                              WHERE name = 'is_retired'
                              LIMIT 1;
                              """;

        if (await command.ExecuteScalarAsync(cancellationToken) is null){
            command.CommandText = """
                                  ALTER TABLE steam_apps
                                  ADD COLUMN is_retired INTEGER NOT NULL DEFAULT 0
                                      CHECK (is_retired IN (0, 1));
                                  """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = """
                              SELECT 1
                              FROM pragma_table_info('steam_apps')
                              WHERE name = 'retirement_discord_message_id'
                              LIMIT 1;
                              """;

        if (await command.ExecuteScalarAsync(cancellationToken) is null){
            command.CommandText = """
                                  ALTER TABLE steam_apps
                                  ADD COLUMN retirement_discord_message_id TEXT NULL;
                                  """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = """
                              SELECT 1
                              FROM pragma_table_info('steam_apps')
                              WHERE name = 'retirement_observed_change'
                              LIMIT 1;
                              """;

        if (await command.ExecuteScalarAsync(cancellationToken) is not null) return;

        command.CommandText = """
                              ALTER TABLE steam_apps
                              ADD COLUMN retirement_observed_change INTEGER NULL
                                  CHECK (retirement_observed_change BETWEEN 0 AND 4294967295);
                              """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static TrackedSteamApp ReadApp(SqliteDataReader reader){
        var messageId = reader.IsDBNull(6) ? null : reader.GetString(6);
        var nextCheck = reader.IsDBNull(9) ? null : reader.GetString(9);
        var retirementMessageId = reader.IsDBNull(11) ? null : reader.GetString(11);
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
            IsRetired = reader.GetInt64(10) != 0,
            RetirementDiscordMessageId = retirementMessageId is null ? null : ulong.Parse(retirementMessageId, CultureInfo.InvariantCulture),
            RetirementObservedChange = reader.IsDBNull(12) ? null : checked((uint)reader.GetInt64(12)),
        };
        var validationError = GetValidationError(app);

        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (validationError is not null) throw new InvalidDataException($"Stored Steam AppID {app.AppId} is invalid: {validationError}");

        return app;
    }

    private static void ValidateForWrite(TrackedSteamApp app){
        ArgumentNullException.ThrowIfNull(app);

        var validationError = GetValidationError(app);

        if (validationError is not null) throw new ArgumentException($"Tracked Steam app is invalid: {validationError}", nameof(app));
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? GetValidationError(TrackedSteamApp app){
        if (app.AppId == 0) return "AppID must be non-zero.";

        if (!Enum.IsDefined(app.Kind)) return $"kind value {(int)app.Kind} is unknown.";

        if (!Enum.IsDefined(app.Status)) return $"status value {(int)app.Status} is unknown.";

        if (app.LastSeenChange < app.FirstSeenChange) return "last-seen change number precedes the first-seen change number.";

        if (app.FirstSeenUtc == default) return "first-seen timestamp is missing.";

        if (app.UpdatedUtc == default) return "updated timestamp is missing.";

        if (app.DiscordMessageId == 0) return "Discord message ID must be non-zero when present.";

        if (app.RetirementDiscordMessageId == 0) return "Retirement Discord message ID must be non-zero when present.";

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
                                   next_metadata_check_utc,
                                   is_retired,
                                   retirement_discord_message_id,
                                   retirement_observed_change
                                   """;
}