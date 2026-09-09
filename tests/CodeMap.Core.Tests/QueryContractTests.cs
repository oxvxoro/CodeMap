using CodeMap.Storage;
using CodeMap.Storage.Queries;
using CodeMap.Web;

namespace CodeMap.Core.Tests;

public sealed class QueryContractTests
{
    [Fact]
    public void QueryLimits_KeepTransportDefaultsCentralized()
    {
        Assert.Equal(20, QueryLimits.DefaultMaxResults);
        Assert.Equal(4, QueryLimits.FlowDefaultDepth);
        Assert.Equal(1, QueryLimits.NormalizeMaxResults(0));
        Assert.Equal(0, QueryLimits.NormalizeImpactDepth(-1));
        Assert.Equal(8, QueryLimits.ClampFlowDepth(100));
    }

    [Theory]
    [InlineData("code", true)]
    [InlineData("app", true)]
    [InlineData("other", false)]
    public void QueryValidation_RejectsUnknownProfiles(string value, bool expected) =>
        Assert.Equal(expected, QueryValidation.IsValidImpactProfile(value));

    [Fact]
    public void SqliteBatch_ReservesFixedVariables()
    {
        var values = Enumerable.Range(0, 1000);
        var chunks = SqliteBatch.ChunkForVariables(values, variablesPerItem: 2, fixedVariables: 2).ToArray();
        Assert.Equal(3, chunks.Length);
        Assert.Equal(498, chunks[0].Length);
        Assert.Equal(998, chunks[0].Length * 2 + 2);
    }

    [Fact]
    public void WebPolicy_ExposesTheDocumentedConfidenceTiers()
    {
        Assert.Equal(0.90, WebConfidencePolicy.DirectImport);
        Assert.Equal(0.85, WebConfidencePolicy.Reexport);
        Assert.Equal(0.70, WebConfidencePolicy.SameFile);
        Assert.Equal(0.60, WebConfidencePolicy.NameFallback);
    }
}
