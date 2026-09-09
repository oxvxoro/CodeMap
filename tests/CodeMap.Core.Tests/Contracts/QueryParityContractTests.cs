using CodeMap.Storage;

namespace CodeMap.Core.Tests.Contracts;

[Collection("MsBuild")]
public sealed class QueryParityContractTests
{
    [Fact]
    public async Task PublicQueryMatrix_PreservesOrderedResultsAndRelationMetadata()
    {
        await using var fixture = await QueryParityFixture.CreateAsync("MultiProject");
        var snapshot = fixture.SnapshotService;
        var sql = fixture.SqlService;
        var greeter = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Greeter");
        var greet = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Greet");
        var caller = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Call");
        var interfaceSymbol = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "IGreeter");

        QueryParityAssert.SameSymbols(snapshot.Find("Gree", 2), sql.Find("Gree", 2));
        QueryParityAssert.SameSymbols(
            snapshot.ResolveSymbol("Greeter", callableOnly: false, maxResults: 20).Matches,
            sql.ResolveSymbol("Greeter", callableOnly: false, maxResults: 20).Matches);
        QueryParityAssert.SameSymbols(snapshot.Members(greeter, 2), sql.Members(greeter, 2));
        QueryParityAssert.SameRelations(snapshot.CallerRelations(greet, 20), sql.CallerRelations(greet, 20));
        QueryParityAssert.SameRelations(snapshot.ImplementationRelations(interfaceSymbol, 20), sql.ImplementationRelations(interfaceSymbol, 20));
        QueryParityAssert.SameImpact(snapshot.Impact(caller, 2, 20), sql.Impact(caller, 2, 20));
    }
}
