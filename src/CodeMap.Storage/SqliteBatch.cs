namespace CodeMap.Storage;

/// <summary>
/// Centralizes SQLite variable-limit batching. SQLite defaults to 999 bound
/// variables on many supported runtimes, so callers reserve variables used by
/// the surrounding statement and describe how many variables each item adds.
/// </summary>
internal static class SqliteBatch
{
    public const int DefaultVariableLimit = 999;

    public static IEnumerable<T[]> ChunkForVariables<T>(
        IEnumerable<T> values,
        int variablesPerItem,
        int fixedVariables)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (variablesPerItem <= 0)
            throw new ArgumentOutOfRangeException(nameof(variablesPerItem));
        if (fixedVariables < 0 || fixedVariables >= DefaultVariableLimit)
            throw new ArgumentOutOfRangeException(nameof(fixedVariables));

        var capacity = Math.Max(1, (DefaultVariableLimit - fixedVariables) / variablesPerItem);
        return values.Chunk(capacity);
    }
}
