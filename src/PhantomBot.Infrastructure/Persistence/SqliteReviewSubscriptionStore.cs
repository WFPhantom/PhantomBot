using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Persistence;

public sealed class SqliteReviewSubscriptionStore(IOptions<PhantomBotOptions> options, IHostEnvironment environment) : IReviewSubscriptionStore{
    private const string SubscriptionColumns = """
                                               id,
                                               source_kind,
                                               source_id,
                                               source_name,
                                               source_url,
                                               status,
                                               created_utc,
                                               next_check_utc,
                                               last_successful_check_utc,
                                               consecutive_failures,
                                               last_error
                                               """;

    private readonly string _connectionString = CreateConnectionString(options.Value.DatabasePath, environment.ContentRootPath);

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
                              CREATE TABLE IF NOT EXISTS review_subscriptions (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  source_kind INTEGER NOT NULL
                                      CHECK (source_kind IN (0, 1)),
                                  source_id TEXT NOT NULL,
                                  source_name TEXT NOT NULL,
                                  source_url TEXT NOT NULL,
                                  status INTEGER NOT NULL
                                      CHECK (status IN (0, 1)),
                                  created_utc TEXT NOT NULL,
                                  next_check_utc TEXT NOT NULL,
                                  last_successful_check_utc TEXT NULL,
                                  consecutive_failures INTEGER NOT NULL DEFAULT 0
                                      CHECK (consecutive_failures >= 0),
                                  last_error TEXT NULL,
                                  UNIQUE (source_kind, source_id)
                              );

                              CREATE INDEX IF NOT EXISTS ix_review_subscriptions_due
                              ON review_subscriptions(next_check_utc, id);

                              CREATE TABLE IF NOT EXISTS review_seen_apps (
                                  subscription_id INTEGER NOT NULL,
                                  app_id INTEGER NOT NULL
                                      CHECK (app_id BETWEEN 1 AND 4294967295),
                                  PRIMARY KEY (subscription_id, app_id),
                                  FOREIGN KEY (subscription_id)
                                      REFERENCES review_subscriptions(id)
                                      ON DELETE CASCADE
                              );

                              CREATE TABLE IF NOT EXISTS review_outbox (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  subscription_id INTEGER NOT NULL,
                                  app_id INTEGER NOT NULL,
                                  review_json TEXT NOT NULL,
                                  discord_message_id TEXT NULL,
                                  UNIQUE (subscription_id, app_id),
                                  FOREIGN KEY (subscription_id, app_id)
                                      REFERENCES review_seen_apps(subscription_id, app_id)
                                      ON DELETE CASCADE
                              );

                              CREATE INDEX IF NOT EXISTS ix_review_outbox_pending
                              ON review_outbox(id)
                              WHERE discord_message_id IS NULL;
                              """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken) => ReadSubscriptionsAsync(null, -1, cancellationToken);

    public Task<IReadOnlyList<SteamReviewSubscription>> GetDueSubscriptionsAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return ReadSubscriptionsAsync(dueAt, limit, cancellationToken);
    }

    public async Task<bool> TryAddAsync(SteamReviewSource source, DateTimeOffset now, CancellationToken cancellationToken){
        ValidateSource(source);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              INSERT INTO review_subscriptions (
                                  source_kind,
                                  source_id,
                                  source_name,
                                  source_url,
                                  status,
                                  created_utc,
                                  next_check_utc,
                                  last_successful_check_utc,
                                  consecutive_failures,
                                  last_error
                              ) VALUES (
                                  $kind,
                                  $sourceId,
                                  $name,
                                  $url,
                                  $status,
                                  $now,
                                  $now,
                                  NULL,
                                  0,
                                  NULL
                              )
                              ON CONFLICT (source_kind, source_id) DO NOTHING;
                              """;

        AddSourceParameters(command, source);

        command.Parameters.AddWithValue("$status", (int)SteamReviewSubscriptionStatus.Initializing);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> RemoveAsync(long subscriptionId, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM review_subscriptions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", subscriptionId);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int?> SeedReviewPageAsync(long subscriptionId, IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);
        ArgumentNullException.ThrowIfNull(appIds);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var subscription = await ReadSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken);

        if (subscription is null || subscription.Status != SteamReviewSubscriptionStatus.Initializing) return null;

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
                              INSERT INTO review_seen_apps (subscription_id, app_id)
                              VALUES ($subscriptionId, $appId)
                              ON CONFLICT (subscription_id, app_id) DO NOTHING;
                              """;

        command.Parameters.AddWithValue("$subscriptionId", subscriptionId);

        var appIdParameter = command.Parameters.Add("$appId", SqliteType.Integer);
        var addedCount = 0;

        foreach (var appId in appIds){
            cancellationToken.ThrowIfCancellationRequested();

            if (appId == 0) throw new ArgumentException("A review cannot have AppID 0.", nameof(appIds));

            appIdParameter.Value = (long)appId;

            addedCount += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return addedCount;
    }

    public Task<bool> CompleteInitializationAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => CompleteCheckAsync(subscriptionId, source, SteamReviewSubscriptionStatus.Initializing, completedUtc, nextCheckUtc, cancellationToken);

    public async Task<bool> EnqueueNewReviewsAsync(long subscriptionId, IReadOnlyCollection<SteamReview> reviews, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);
        ArgumentNullException.ThrowIfNull(reviews);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var subscription = await ReadSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken);

        if (subscription is null || subscription.Status != SteamReviewSubscriptionStatus.Active) return false;

        await using var seenCommand = connection.CreateCommand();

        seenCommand.Transaction = transaction;

        seenCommand.CommandText = """
                                  INSERT INTO review_seen_apps (subscription_id, app_id)
                                  VALUES ($subscriptionId, $appId)
                                  ON CONFLICT (subscription_id, app_id) DO NOTHING;
                                  """;

        seenCommand.Parameters.AddWithValue("$subscriptionId", subscriptionId);

        var seenAppId = seenCommand.Parameters.Add("$appId", SqliteType.Integer);

        await using var outboxCommand = connection.CreateCommand();

        outboxCommand.Transaction = transaction;

        outboxCommand.CommandText = """
                                    INSERT INTO review_outbox (
                                        subscription_id,
                                        app_id,
                                        review_json,
                                        discord_message_id
                                    ) VALUES (
                                        $subscriptionId,
                                        $appId,
                                        $review,
                                        NULL
                                    );
                                    """;

        outboxCommand.Parameters.AddWithValue("$subscriptionId", subscriptionId);

        var outboxAppId = outboxCommand.Parameters.Add("$appId", SqliteType.Integer);
        var reviewJson = outboxCommand.Parameters.Add("$review", SqliteType.Text);

        foreach (var review in reviews){
            cancellationToken.ThrowIfCancellationRequested();

            ValidateReview(review);

            if (review.Source.Kind != subscription.Source.Kind || review.Source.Id != subscription.Source.Id) throw new ArgumentException("Every review must belong to the specified subscription's source.", nameof(reviews));

            seenAppId.Value = (long)review.AppId;

            if (await seenCommand.ExecuteNonQueryAsync(cancellationToken) == 0) continue;

            outboxAppId.Value = (long)review.AppId;
            reviewJson.Value = JsonSerializer.Serialize(review);

            await outboxCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public Task<bool> CompletePollAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => CompleteCheckAsync(subscriptionId, source, SteamReviewSubscriptionStatus.Active, completedUtc, nextCheckUtc, cancellationToken);

    public async Task<bool> RecordFailureAsync(long subscriptionId, string failureReason, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
                              UPDATE review_subscriptions
                              SET consecutive_failures = consecutive_failures + 1,
                                  last_error = $failureReason,
                                  next_check_utc = $nextCheck
                              WHERE id = $id;
                              """;

        command.Parameters.AddWithValue("$id", subscriptionId);
        command.Parameters.AddWithValue("$failureReason", failureReason.Trim());
        command.Parameters.AddWithValue("$nextCheck", FormatTimestamp(nextCheckUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PendingSteamReview>> GetPendingReviewsAsync(int limit, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              SELECT pending.id,
                                     pending.subscription_id,
                                     pending.app_id,
                                     pending.review_json,
                                     subscription.source_kind,
                                     subscription.source_id
                              FROM review_outbox AS pending
                              INNER JOIN review_subscriptions AS subscription
                                  ON subscription.id = pending.subscription_id
                              WHERE pending.discord_message_id IS NULL
                                AND subscription.status = $active
                              ORDER BY pending.id
                              LIMIT $limit;
                              """;

        command.Parameters.AddWithValue("$active", (int)SteamReviewSubscriptionStatus.Active);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var pendingReviews = new List<PendingSteamReview>();

        while (await reader.ReadAsync(cancellationToken)){
            var pendingId = reader.GetInt64(0);
            var subscriptionId = reader.GetInt64(1);
            var appId = checked((uint)reader.GetInt64(2));

            SteamReview review;

            try{
                review = JsonSerializer.Deserialize<SteamReview>(reader.GetString(3)) ?? throw new InvalidDataException($"Pending review {pendingId} contains a null payload.");

                ValidateReview(review);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException){
                throw new InvalidDataException($"Pending review {pendingId} contains an invalid payload.", exception);
            }

            var sourceKind = (SteamReviewSourceKind)reader.GetInt32(4);
            var sourceId = ulong.Parse(reader.GetString(5), NumberStyles.None, CultureInfo.InvariantCulture);

            if (review.AppId != appId || review.Source.Kind != sourceKind || review.Source.Id != sourceId) throw new InvalidDataException($"Pending review {pendingId} does not match its stored app or subscription.");

            pendingReviews.Add(new PendingSteamReview(pendingId, subscriptionId, review));
        }

        return pendingReviews;
    }

    public async Task<bool> MarkReviewPostedAsync(long pendingReviewId, ulong discordMessageId, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pendingReviewId);

        if (discordMessageId == 0) throw new ArgumentOutOfRangeException(nameof(discordMessageId), "A Discord message ID must be non-zero.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              UPDATE review_outbox
                              SET discord_message_id = $messageId
                              WHERE id = $id
                                AND discord_message_id IS NULL;
                              """;

        command.Parameters.AddWithValue("$id", pendingReviewId);
        command.Parameters.AddWithValue("$messageId", discordMessageId.ToString(CultureInfo.InvariantCulture));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<IReadOnlyList<SteamReviewSubscription>> ReadSubscriptionsAsync(DateTimeOffset? dueAt, int limit, CancellationToken cancellationToken){
        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        var filter = dueAt is null ? string.Empty : "WHERE next_check_utc <= $dueAt";

        command.CommandText = $"""
                               SELECT {SubscriptionColumns}
                               FROM review_subscriptions
                               {filter}
                               ORDER BY next_check_utc, id
                               LIMIT $limit;
                               """;

        if (dueAt is not null) command.Parameters.AddWithValue("$dueAt", FormatTimestamp(dueAt.Value));

        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var subscriptions = new List<SteamReviewSubscription>();

        while (await reader.ReadAsync(cancellationToken)) subscriptions.Add(ReadSubscription(reader));

        return subscriptions;
    }

    private async Task<bool> CompleteCheckAsync(long subscriptionId, SteamReviewSource source, SteamReviewSubscriptionStatus expectedStatus, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriptionId);

        ValidateSource(source);

        if (nextCheckUtc < completedUtc) throw new ArgumentException("The next review check cannot precede the completed check.", nameof(nextCheckUtc));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
                              UPDATE review_subscriptions
                              SET source_name = $name,
                                  source_url = $url,
                                  status = $active,
                                  next_check_utc = $nextCheck,
                                  last_successful_check_utc = $completed,
                                  consecutive_failures = 0,
                                  last_error = NULL
                              WHERE id = $id
                                AND status = $expectedStatus
                                AND source_kind = $kind
                                AND source_id = $sourceId;
                              """;

        AddSourceParameters(command, source);

        command.Parameters.AddWithValue("$id", subscriptionId);
        command.Parameters.AddWithValue("$expectedStatus", (int)expectedStatus);
        command.Parameters.AddWithValue("$active", (int)SteamReviewSubscriptionStatus.Active);
        command.Parameters.AddWithValue("$nextCheck", FormatTimestamp(nextCheckUtc));
        command.Parameters.AddWithValue("$completed", FormatTimestamp(completedUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<SteamReviewSubscription?> ReadSubscriptionAsync(SqliteConnection connection, SqliteTransaction transaction, long subscriptionId, CancellationToken cancellationToken){
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""
                               SELECT {SubscriptionColumns}
                               FROM review_subscriptions
                               WHERE id = $id;
                               """;

        command.Parameters.AddWithValue("$id", subscriptionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadSubscription(reader) : null;
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static SteamReviewSubscription ReadSubscription(SqliteDataReader reader){
        var source = new SteamReviewSource{
            Kind = (SteamReviewSourceKind)reader.GetInt32(1),
            Id = ulong.Parse(reader.GetString(2), NumberStyles.None, CultureInfo.InvariantCulture),
            Name = reader.GetString(3),
            Url = reader.GetString(4),
        };

        var status = (SteamReviewSubscriptionStatus)reader.GetInt32(5);

        if (!Enum.IsDefined(status)) throw new InvalidDataException("A stored review subscription has an unknown status.");

        try{
            ValidateSource(source);
        }
        catch (ArgumentException exception){
            throw new InvalidDataException("A stored review subscription has an invalid source.", exception);
        }

        return new SteamReviewSubscription{
            Id = reader.GetInt64(0),
            Source = source,
            Status = status,
            CreatedUtc = ParseTimestamp(reader.GetString(6)),
            NextCheckUtc = ParseTimestamp(reader.GetString(7)),
            LastSuccessfulCheckUtc = reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            ConsecutiveFailures = reader.GetInt32(9),
            LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
        };
    }

    private static void AddSourceParameters(SqliteCommand command, SteamReviewSource source){
        command.Parameters.AddWithValue("$kind", (int)source.Kind);
        command.Parameters.AddWithValue("$sourceId", source.Id.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$url", source.Url);
    }

    private static void ValidateSource(SteamReviewSource source){
        ArgumentNullException.ThrowIfNull(source);

        if (!Enum.IsDefined(source.Kind)) throw new ArgumentException("The review source kind is invalid.", nameof(source));

        if (source.Id == 0) throw new ArgumentException("The review source ID must be non-zero.", nameof(source));

        if (source is{ Kind: SteamReviewSourceKind.Curator, Id: > uint.MaxValue }) throw new ArgumentException("A curator ID must fit in an unsigned 32-bit integer.", nameof(source));

        if (string.IsNullOrWhiteSpace(source.Name)) throw new ArgumentException("The review source must have a display name.", nameof(source));

        if (!IsWebUrl(source.Url)) throw new ArgumentException("The review source must have an HTTP or HTTPS URL.", nameof(source));
    }

    private static void ValidateReview(SteamReview review){
        ArgumentNullException.ThrowIfNull(review);

        ValidateSource(review.Source);

        if (review.AppId == 0) throw new ArgumentException("A review must have a non-zero AppID.", nameof(review));

        if (!Enum.IsDefined(review.Recommendation)) throw new ArgumentException("The review recommendation is invalid.", nameof(review));

        ArgumentNullException.ThrowIfNull(review.Text);

        if (!IsWebUrl(review.Url)) throw new ArgumentException("A review must have an HTTP or HTTPS URL.", nameof(review));

        if (review.ExternalReviewUrl is not null && !IsWebUrl(review.ExternalReviewUrl)) throw new ArgumentException("The external review URL is invalid.", nameof(review));

        if (review.ThumbnailUrl is not null && !IsWebUrl(review.ThumbnailUrl)) throw new ArgumentException("The review thumbnail URL is invalid.", nameof(review));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken){
        var connection = new SqliteConnection(_connectionString);

        try{
            await connection.OpenAsync(cancellationToken);

            return connection;
        }
        catch{
            await connection.DisposeAsync();

            throw;
        }
    }

    private static string CreateConnectionString(string databasePath, string contentRootPath){
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var fullPath = Path.GetFullPath(databasePath, contentRootPath);

        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The database path must have a parent directory.");

        Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder{
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    private static bool IsWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}