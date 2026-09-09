using CodeMap.Core.Contracts;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class GraphReaderContractTests
{
    [Fact]
    public void StorageQueryService_ImplementsCoreGraphReaderPort()
    {
        Assert.True(typeof(ICodeMapGraphReader).IsAssignableFrom(typeof(CodeMapQueryService)));
    }
}
