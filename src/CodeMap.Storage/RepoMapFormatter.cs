using CodeMap.Core.Models;

namespace CodeMap.Storage;

public static class RepoMapFormatter
{
    public static string ToMermaid(
        CodeMapSnapshot graph,
        IReadOnlyDictionary<string, string[]> projectReferences,
        int maxNodes = 40)
    {
        var lines = new List<string> { "flowchart LR" };
        foreach (var (project, references) in projectReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var from = Sanitize(project);
            lines.Add($"  {from}[\"{Escape(project)}\"]");
            foreach (var reference in references)
                lines.Add($"  {from} --> {Sanitize(reference)}");
        }

        var calls = graph.Edges
            .Where(edge => edge.Kind == EdgeKind.Calls)
            .Take(maxNodes)
            .ToArray();
        var byId = graph.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        foreach (var edge in calls)
        {
            if (!byId.TryGetValue(edge.SourceId, out var source) || !byId.TryGetValue(edge.TargetId, out var target))
                continue;
            lines.Add($"  {Sanitize(source.Id)}[\"{Escape(source.Name)}\"] --> {Sanitize(target.Id)}[\"{Escape(target.Name)}\"]");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string ToHtml(string mermaid, RiskScore? risk, IReadOnlyList<ArchitectureViolation> violations)
    {
        var riskLine = risk is null ? "" : $"<p>Risk: <strong>{risk.Level}</strong> (publicApi={risk.PublicApis}, callers={risk.Callers}, crossProject={risk.CrossProject}, untested={risk.Untested})</p>";
        var violationHtml = violations.Count == 0
            ? "<p>No architecture violations.</p>"
            : "<ul>" + string.Join("", violations.Select(item => $"<li>{EscapeHtml(item.Kind)}: {EscapeHtml(item.Message)}</li>")) + "</ul>";
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>CodeMap report</title>
              <style>body{font-family:sans-serif;margin:2rem;max-width:960px}pre{background:#f6f8fa;padding:1rem;overflow:auto}</style>
            </head>
            <body>
              <h1>CodeMap report</h1>
              {{riskLine}}
              <h2>Architecture</h2>
              {{violationHtml}}
              <h2>Graph</h2>
              <pre>{{EscapeHtml(mermaid)}}</pre>
            </body>
            </html>
            """;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return chars.Length == 0 ? "n" : new string(chars);
    }

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);
    private static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
