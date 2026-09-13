using Microsoft.Data.Sqlite;

namespace PhantomBot.Infrastructure.Persistence;

internal static class SqliteStoreConnection{
    public static string CreateConnectionString(string databasePath, string contentRootPath){
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

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken cancellationToken){
        var connection = new SqliteConnection(connectionString);

        try{
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA synchronous = FULL;";

            await command.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch{
            await connection.DisposeAsync();

            throw;
        }
    }
}