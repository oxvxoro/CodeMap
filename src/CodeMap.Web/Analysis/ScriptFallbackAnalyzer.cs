namespace CodeMap.Web;

/// <summary>Explicit fallback policy seam for unsupported script syntax.</summary>
public static class ScriptFallbackAnalyzer
{
    public static bool IsRequired(ScriptAnalysisMode mode) => RegexScriptFallbackAnalyzer.IsFallbackRequired(mode);
}
