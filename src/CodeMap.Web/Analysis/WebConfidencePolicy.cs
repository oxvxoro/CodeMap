namespace CodeMap.Web;

internal static class WebConfidencePolicy
{
    public const double DirectImport = 0.90;
    public const double Reexport = 0.85;
    public const double SameFile = 0.70;
    public const double NameFallback = 0.60;
    public const double CssSelector = 0.85;
    public const double DomSelector = 0.60;
}

public enum ScriptAnalysisMode
{
    Ast,
    RegexFallback
}
