using Microsoft.Data.Sqlite;

namespace CodeMap.Storage.Migrations;

public sealed class CodeMapMigrator
{
    private readonly IReadOnlyList<ICodeMapMigration> _migrations;

    public CodeMapMigrator(IEnumerable<ICodeMapMigration>? migrations = null) =>
        _migrations = (migrations ?? [new Migration0004Baseline()]).ToArray();

    public async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = "CREATE TABLE IF NOT EXISTS codemap_migrations (id TEXT PRIMARY KEY, version TEXT NOT NULL, applied_at_utc TEXT NOT NULL)";
            await metadata.ExecuteNonQueryAsync(cancellationToken);
        }

        var current = await ReadVersionAsync(connection, cancellationToken);
        foreach (var migration in _migrations.OrderBy(item => int.Parse(item.ToVersion)))
        {
            if (int.Parse(migration.ToVersion) <= current)
                continue;
            if (migration.FromVersion != current.ToString())
                throw new InvalidOperationException(
                    $"CodeMap schema migration cannot advance from '{current}' to '{migration.ToVersion}'.");

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await migration.ApplyAsync(connection, transaction, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO codemap_migrations(id, version, applied_at_utc) VALUES($id, $version, $appliedAt)";
            command.Parameters.AddWithValue("$id", migration.Id);
            command.Parameters.AddWithValue("$version", migration.ToVersion);
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            current = int.Parse(migration.ToVersion);
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM codemap_migrations ORDER BY CAST(version AS INTEGER) DESC LIMIT 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string version && int.TryParse(version, out var parsed) ? parsed : 0;
    }
}
