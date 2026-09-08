using CodeMap.Core.Models;

namespace CodeMap.Scip;

public sealed class ScipSymbolMapper
{
    public const string AnalyzerVersion = "1";

    public string CreateId(string importName, string scipSymbol) =>
        $"scip://{Uri.EscapeDataString(importName)}/{Uri.EscapeDataString(scipSymbol)}";

    public NodeKind? MapKind(ScipSymbolKind kind) => kind switch
    {
        ScipSymbolKind.Namespace => NodeKind.Namespace,
        ScipSymbolKind.Class => NodeKind.Class,
        ScipSymbolKind.Interface => NodeKind.Interface,
        ScipSymbolKind.Struct => NodeKind.Struct,
        ScipSymbolKind.Enum => NodeKind.Enum,
        ScipSymbolKind.Method => NodeKind.Method,
        ScipSymbolKind.Constructor => NodeKind.Constructor,
        ScipSymbolKind.Function => NodeKind.Function,
        ScipSymbolKind.Property => NodeKind.Property,
        ScipSymbolKind.Field => NodeKind.Field,
        ScipSymbolKind.Event => NodeKind.Event,
        _ => null
    };

    public string DisplayName(ScipSymbolInformation symbol) =>
        string.IsNullOrWhiteSpace(symbol.DisplayName) ? symbol.Symbol : symbol.DisplayName;
}
