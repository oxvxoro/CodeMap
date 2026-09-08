using CodeMap.Scip;
using CodeMap.Scip.Protocol;
using Google.Protobuf;
using ProtoIndex = CodeMap.Scip.Protocol.Index;

namespace CodeMap.Core.Tests;

public sealed class ScipIndexReaderTests
{
    [Fact]
    public async Task ReadAsync_ConvertsTypedRangesAndDefinitionRoles()
    {
        var path = Path.Combine(Path.GetTempPath(), "codemap-scip-reader-" + Guid.NewGuid() + ".scip");
        var symbol = "scip-python python pkg 1.0 run().";
        var index = new ProtoIndex
        {
            Documents =
            {
                new Document
                {
                    RelativePath = "app.py",
                    Language = "python",
                    Symbols = { new SymbolInformation { Symbol = symbol, DisplayName = "run", Kind = 17 } },
                    Occurrences =
                    {
                        new Occurrence
                        {
                            Symbol = symbol,
                            SymbolRoles = 1,
                            SingleLineRange = new SingleLineRange { Line = 2, StartCharacter = 4, EndCharacter = 7 },
                            MultiLineEnclosingRange = new MultiLineRange { StartLine = 1, StartCharacter = 0, EndLine = 3, EndCharacter = 0 }
                        }
                    }
                }
            }
        };
        await File.WriteAllBytesAsync(path, index.ToByteArray());
        try
        {
            var result = await new ScipIndexReader().ReadAsync(path);

            var document = Assert.Single(result.Documents);
            var occurrence = Assert.Single(document.Occurrences);
            Assert.True(occurrence.IsDefinition);
            Assert.Equal(new ScipRange(2, 4, 2, 7), occurrence.Range);
            Assert.Equal(new ScipRange(1, 0, 3, 0), occurrence.EnclosingRange);
            Assert.Equal(ScipSymbolKind.Function, Assert.Single(document.Symbols).Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
