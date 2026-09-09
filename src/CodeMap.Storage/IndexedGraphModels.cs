using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

internal static class IndexedEdgeReader
{







    internal static IndexedEdge Read(SqliteDataReader reader, int offset = 0) => new(
        reader.GetString(offset),
        reader.GetString(offset + 1),
        Enum.Parse<EdgeKind>(reader.GetString(offset + 2)),
        reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
        ParseResolutionKind(reader.GetString(offset + 5)),
        reader.IsDBNull(offset + 6) ? null : reader.GetDouble(offset + 6));





    internal static IndexedEdge ReadWithSpan(SqliteDataReader reader, int offset = 0)
    {
        var edge = Read(reader, offset);
        return edge with
        {
            StartColumn = reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7),
            EndLine = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
            EndColumn = reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9)
        };
    }

    private static EdgeResolutionKind ParseResolutionKind(string value) =>
        Enum.Parse<EdgeResolutionKind>(value, ignoreCase: true);
}
