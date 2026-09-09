using Microsoft.Data.Sqlite;

namespace CodeMap.Storage.Migrations;

/// <summary>
/// Names the schema that shipped before incremental migrations were introduced.
/// The baseline has no destructive operation; it only records the migration
/// ledger after the existing schema initializer has run.
/// </summary>
public sealed class Migration0004Baseline : ICodeMapMigration
{
    public string Id => "0004-baseline";

    public string FromVersion => "0";

    public string ToVersion => "4";

    public Task ApplyAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
