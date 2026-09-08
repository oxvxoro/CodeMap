using CodeMap.Core.Models;
using CodeMap.Scip;

namespace CodeMap.Core.Tests;

public sealed class ScipGraphMapperTests
{
    [Fact]
    public void Map_ProducesDeterministicDefinitionsAndSemanticReferencesWithoutCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-scip-mapper-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "app.py");
        File.WriteAllText(sourcePath, "def run():\n    return normalize()\n\ndef normalize():\n    return 1\n");
        try
        {
            var run = "scip-python python pkg 1.0 run().";
            var normalize = "scip-python python pkg 1.0 normalize().";
            var index = new ScipIndex([
                new ScipDocument("app.py", "python",
                [
                    new ScipOccurrence(run, new ScipRange(0, 4, 0, 7), true),
                    new ScipOccurrence(normalize, new ScipRange(1, 11, 1, 20), false, run),
                    new ScipOccurrence(normalize, new ScipRange(3, 4, 3, 13), true)
                ],
                [
                    new ScipSymbolInformation(run, "run", ScipSymbolKind.Function,
                        [new ScipRelationship(normalize, IsReference: false, IsImplementation: true, IsTypeDefinition: false, IsDefinition: false)]),
                    new ScipSymbolInformation(normalize, "normalize", ScipSymbolKind.Function)
                ])
            ]);

            var project = new ScipGraphMapper().Map("backend-python", root, index);

            Assert.Equal("scip:backend-python", project.ProjectName);
            Assert.Equal(2, project.Result.Nodes.Count(node => node.Kind == NodeKind.Function));
            Assert.Contains(project.Result.Edges, edge => edge.Kind == EdgeKind.References && edge.Confidence == 1.0);
            Assert.Contains(project.Result.Edges, edge => edge.Kind == EdgeKind.Implements && edge.Confidence == 1.0);
            Assert.Contains(project.Result.Edges, edge => edge.Kind == EdgeKind.ImplementedBy && edge.Confidence == 1.0);
            Assert.DoesNotContain(project.Result.Edges, edge => edge.Kind == EdgeKind.Calls);
            Assert.All(project.Result.Nodes.Where(node => node.Kind == NodeKind.Function), node => Assert.StartsWith("scip://backend-python/", node.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
