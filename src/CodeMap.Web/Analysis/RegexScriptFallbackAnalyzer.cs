namespace CodeMap.Web;

internal static class RegexScriptFallbackAnalyzer
{
    public static bool IsFallbackRequired(ScriptAnalysisMode mode) => mode == ScriptAnalysisMode.RegexFallback;
}
