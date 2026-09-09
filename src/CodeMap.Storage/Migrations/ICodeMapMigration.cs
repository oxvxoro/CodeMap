using Microsoft.Data.Sqlite;

namespace CodeMap.Storage.Migrations;

public interface ICodeMapMigration
{
    string Id { get; }

    string FromVersion { get; }

    string ToVersion { get; }

    Task ApplyAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken);
}
