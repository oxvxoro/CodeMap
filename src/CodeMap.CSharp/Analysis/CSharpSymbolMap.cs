using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

/// <summary>Stable lookup view shared by staged C# collectors.</summary>
public sealed class CSharpSymbolMap
{
    private readonly IReadOnlyDictionary<string, CodeNode> _byId;

    public CSharpSymbolMap(IEnumerable<CodeNode> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        _byId = declarations.Where(node => node.Kind != NodeKind.File)
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<CodeNode> Declarations => _byId.Values.ToArray();
    public bool TryGet(string id, out CodeNode? node) => _byId.TryGetValue(id, out node);
}
