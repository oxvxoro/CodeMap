namespace CodeMap.Web;

public static class ScriptDocumentAnalyzer
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".js" or ".jsx" or ".ts" or ".tsx";
}
