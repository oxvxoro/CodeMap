namespace CodeMap.Web;

public static class CssDocumentAnalyzer
{
    public static bool Supports(string path) => string.Equals(Path.GetExtension(path), ".css", StringComparison.OrdinalIgnoreCase);
}
