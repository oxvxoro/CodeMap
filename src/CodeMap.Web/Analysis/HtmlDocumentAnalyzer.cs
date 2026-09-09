namespace CodeMap.Web;

public static class HtmlDocumentAnalyzer
{
    public static bool Supports(string path) => string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".htm", StringComparison.OrdinalIgnoreCase);
}
