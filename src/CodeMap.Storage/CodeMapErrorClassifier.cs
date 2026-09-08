namespace CodeMap.Storage;

public static class CodeMapErrorClassifier
{
    public static (string Code, string Message) Classify(Exception exception) => exception switch
    {
        FileNotFoundException fileNotFound when fileNotFound.Message.Contains("No CodeMap index found", StringComparison.Ordinal)
            => ("index_not_found", "No CodeMap index found.\nRun: codemap index"),
        IndexBuildingException building
            => ("index_building", building.Message),
        InvalidOperationException invalidOperation when invalidOperation.Message.Contains("CodeMap index schema is outdated", StringComparison.Ordinal)
            => ("schema_outdated", invalidOperation.Message),
        GitUnavailableException git
            => ("git_unavailable", git.Message),
        SemanticSliceException slice
            => (slice.Code, slice.Message),
        _
            => ("query_failed", $"codemap query failed: {exception.Message}")
    };
}
