using CodeMap.Scip.Protocol;
using ProtocolIndex = CodeMap.Scip.Protocol.Index;

namespace CodeMap.Scip;

public sealed class ScipIndexReader
{
    private const int DefinitionRole = 0x1;
    private const long MaxArtifactBytes = 512L * 1024 * 1024;
    private const int MaxDocuments = 200_000;
    private const int MaxOccurrencesOrSymbols = 5_000_000;

    public async Task<ScipIndex> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (info.Length > MaxArtifactBytes)
            throw new InvalidDataException($"SCIP artifact exceeds the {MaxArtifactBytes / 1024 / 1024} MiB import limit.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Read(bytes);
    }

    /// <summary>Parses an already-loaded SCIP artifact using the same structural limits as file import.</summary>
    public ScipIndex Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > MaxArtifactBytes)
            throw new InvalidDataException($"SCIP artifact exceeds the {MaxArtifactBytes / 1024 / 1024} MiB import limit.");
        var index = ProtocolIndex.Parser.ParseFrom(bytes);
        if (index.Documents.Count > MaxDocuments
            || index.Documents.Sum(document => (long)document.Occurrences.Count + document.Symbols.Count) > MaxOccurrencesOrSymbols)
            throw new InvalidDataException("SCIP artifact exceeds the document or occurrence/symbol import limit.");
        return Convert(index);
    }

    internal static ScipIndex Convert(ProtocolIndex index) => new(index.Documents
        .Select(document => new ScipDocument(
            document.RelativePath,
            document.Language,
            document.Occurrences
                .Where(occurrence => !string.IsNullOrWhiteSpace(occurrence.Symbol))
                .Select(occurrence => new ScipOccurrence(
                    occurrence.Symbol,
                    ToRange(occurrence),
                    (occurrence.SymbolRoles & DefinitionRole) != 0,
                    EnclosingRange: ToEnclosingRange(occurrence)))
                .ToArray(),
            document.Symbols
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol.Symbol))
                .Select(symbol => new ScipSymbolInformation(symbol.Symbol, symbol.DisplayName, ToKind(symbol.Kind),
                    symbol.Relationships.Select(relationship => new ScipRelationship(relationship.Symbol,
                        relationship.IsReference, relationship.IsImplementation, relationship.IsTypeDefinition, relationship.IsDefinition)).ToArray()))
                .ToArray()))
        .ToArray());

    private static ScipSymbolKind ToKind(int kind) => kind switch
    {
        7 => ScipSymbolKind.Class,
        9 => ScipSymbolKind.Constructor,
        11 => ScipSymbolKind.Enum,
        13 => ScipSymbolKind.Event,
        15 => ScipSymbolKind.Field,
        17 => ScipSymbolKind.Function,
        21 => ScipSymbolKind.Interface,
        26 or 66 or 67 or 68 or 69 or 70 or 71 or 72 or 74 or 76 or 80 => ScipSymbolKind.Method,
        29 or 30 or 35 => ScipSymbolKind.Namespace,
        37 or 38 or 44 or 52 => ScipSymbolKind.Parameter,
        41 or 45 or 81 => ScipSymbolKind.Property,
        49 or 59 => ScipSymbolKind.Struct,
        58 => ScipSymbolKind.TypeParameter,
        54 => ScipSymbolKind.Struct,
        _ => ScipSymbolKind.Unknown
    };

    private static ScipRange ToRange(Occurrence occurrence) => occurrence.TypedRangeCase switch
    {
        Occurrence.TypedRangeOneofCase.SingleLineRange => ToRange(occurrence.SingleLineRange),
        Occurrence.TypedRangeOneofCase.MultiLineRange => ToRange(occurrence.MultiLineRange),
        _ => ToRange(occurrence.Range)
    };

    private static ScipRange? ToEnclosingRange(Occurrence occurrence) => occurrence.TypedEnclosingRangeCase switch
    {
        Occurrence.TypedEnclosingRangeOneofCase.SingleLineEnclosingRange => ToRange(occurrence.SingleLineEnclosingRange),
        Occurrence.TypedEnclosingRangeOneofCase.MultiLineEnclosingRange => ToRange(occurrence.MultiLineEnclosingRange),
        _ => occurrence.EnclosingRange.Count == 0 ? null : ToRange(occurrence.EnclosingRange)
    };

    private static ScipRange ToRange(SingleLineRange range) => new(range.Line, range.StartCharacter, range.Line, range.EndCharacter);
    private static ScipRange ToRange(MultiLineRange range) => new(range.StartLine, range.StartCharacter, range.EndLine, range.EndCharacter);
    private static ScipRange ToRange(IReadOnlyList<int> range) => range.Count switch
    {
        3 => new ScipRange(range[0], range[1], range[0], range[2]),
        4 => new ScipRange(range[0], range[1], range[2], range[3]),
        _ => throw new InvalidDataException("A SCIP occurrence range must contain three or four values.")
    };
}
