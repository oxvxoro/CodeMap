using CodeMap.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.CSharp;

/// <summary>Builds an ephemeral, intraprocedural def/use slice for one executable C# body.</summary>
public sealed class CSharpSemanticSliceAnalyzer
{
    public SemanticSliceAnalysis Analyze(
        SemanticModel semanticModel,
        IMethodSymbol method,
        SemanticSliceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxResults is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxResults must be between 1 and 500.");

        var declaration = method.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken))
            .SingleOrDefault(syntax => GetBody(syntax) is not null)
            ?? throw new InvalidOperationException("The selected symbol has no supported executable body.");
        var body = GetBody(declaration)!;
        var facts = new List<Fact>();
        var definitions = new Dictionary<ISymbol, Fact>(SymbolEqualityComparer.Default);
        var conditions = new Dictionary<IfStatementSyntax, Fact>();

        foreach (var parameter in method.Parameters)
            AddFact(facts, definitions, parameter, Array.Empty<ISymbol>(), SliceOperationKind.Parameter,
                parameter.Name, declaration, SliceDependencyKind.Definition);

        if (body is ArrowExpressionClauseSyntax arrow)
            AddFact(facts, definitions, null, ReadSymbols(semanticModel, arrow.Expression, cancellationToken),
                SliceOperationKind.Return, arrow.Expression.ToString(), arrow.Expression, SliceDependencyKind.Return);

        foreach (var statement in body.DescendantNodesAndSelf().OfType<StatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement != body && statement.Ancestors().Any(node => IsNestedExecutable(node) && node != declaration))
                continue;

            switch (statement)
            {
                case LocalDeclarationStatementSyntax local:
                    foreach (var variable in local.Declaration.Variables)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                        if (symbol is not null)
                            AddFact(facts, definitions, symbol, ReadSymbols(semanticModel, variable.Initializer?.Value, cancellationToken),
                                SliceOperationKind.Declaration, local.ToString(), local, SliceDependencyKind.Assignment);
                    }
                    break;
                case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }:
                    AddAssignment(assignment);
                    break;
                case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }:
                    AddFact(facts, definitions, null, ReadSymbols(semanticModel, invocation, cancellationToken),
                        SliceOperationKind.Invocation, invocation.ToString(), invocation, SliceDependencyKind.Argument);
                    break;
                case ReturnStatementSyntax returned:
                    AddFact(facts, definitions, null, ReadSymbols(semanticModel, returned.Expression, cancellationToken),
                        SliceOperationKind.Return, returned.ToString(), returned, SliceDependencyKind.Return);
                    break;
                case IfStatementSyntax conditional:
                    conditions[conditional] = AddFact(facts, definitions, null, ReadSymbols(semanticModel, conditional.Condition, cancellationToken),
                        SliceOperationKind.Condition, conditional.Condition.ToString(), conditional.Condition, SliceDependencyKind.Condition);
                    break;
            }
        }

        var seeds = SelectSeeds(facts, request);
        var selected = Traverse(facts, seeds, request.Direction);
        var ordered = selected.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key.Item.Location.StartLine)
            .ThenBy(pair => pair.Key.Item.Location.StartColumn).ThenBy(pair => pair.Key.Id).Select(pair => pair.Key).ToList();
        var truncated = ordered.Count > request.MaxResults;
        var items = ordered.Take(request.MaxResults).Select(fact => fact.Item).ToArray();
        var itemIds = items.Select(item => item.Id).ToHashSet();
        return new SemanticSliceAnalysis(items,
            facts.SelectMany(fact => fact.Dependencies).Where(edge => itemIds.Contains(edge.Source) && itemIds.Contains(edge.Target)).Distinct().ToArray(),
            truncated);

        void AddAssignment(AssignmentExpressionSyntax assignment)
        {
            var symbol = semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol;
            if (symbol is null)
                return;
            var conditional = assignment.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            var loopCarried = assignment.Ancestors().Any(node => node is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
            var fact = AddFact(facts, definitions, symbol, ReadSymbols(semanticModel, assignment.Right, cancellationToken),
                SliceOperationKind.Assignment, assignment.ToString(), assignment, SliceDependencyKind.Assignment,
                includePreviousDefinition: conditional is not null || loopCarried);
            if (conditional is not null && conditions.TryGetValue(conditional, out var condition))
                fact.Dependencies.Add(new SliceDependency(condition.Id, fact.Id, SliceDependencyKind.Condition));
        }
    }

    private static SyntaxNode? GetBody(SyntaxNode declaration) => declaration switch
    {
        BaseMethodDeclarationSyntax method => method.Body ?? (SyntaxNode?)method.ExpressionBody,
        LocalFunctionStatementSyntax localFunction => localFunction.Body ?? (SyntaxNode?)localFunction.ExpressionBody,
        AnonymousFunctionExpressionSyntax function => function.Body,
        AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody,
        _ => null
    };

    private static bool IsNestedExecutable(SyntaxNode node) =>
        node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;

    private static Fact AddFact(List<Fact> facts, Dictionary<ISymbol, Fact> definitions, ISymbol? written,
        IEnumerable<ISymbol> reads, SliceOperationKind kind, string display, SyntaxNode syntax, SliceDependencyKind incomingKind,
        bool includePreviousDefinition = false)
    {
        var item = new SliceItem(facts.Count + 1, kind, written?.Name, display, ToLocation(syntax.GetLocation()));
        var fact = new Fact(item);
        foreach (var read in reads.Distinct(SymbolEqualityComparer.Default))
            if (definitions.TryGetValue(read, out var definition))
                fact.Dependencies.Add(new SliceDependency(definition.Id, fact.Id, incomingKind));
        if (includePreviousDefinition && written is not null && definitions.TryGetValue(written, out var previous))
            fact.Dependencies.Add(new SliceDependency(previous.Id, fact.Id, SliceDependencyKind.Definition));
        facts.Add(fact);
        if (written is not null)
            definitions[written] = fact;
        return fact;
    }

    private static IEnumerable<ISymbol> ReadSymbols(SemanticModel model, ExpressionSyntax? expression, CancellationToken cancellationToken) =>
        expression?.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(identifier => model.GetSymbolInfo(identifier, cancellationToken).Symbol)
            .Where(symbol => symbol is ILocalSymbol or IParameterSymbol)
            .Cast<ISymbol>() ?? Enumerable.Empty<ISymbol>();

    private static IReadOnlyCollection<Fact> SelectSeeds(IReadOnlyList<Fact> facts, SemanticSliceRequest request)
    {
        if (request.Line is { } line)
        {
            var matching = facts.Where(fact => fact.Item.Location.StartLine <= line && fact.Item.Location.EndLine >= line).ToArray();
            if (matching.Length > 0)
                return matching;
        }
        return request.Direction == SliceDirection.Backward
            ? facts.Where(fact => fact.Item.Kind == SliceOperationKind.Return).ToArray()
            : facts.Where(fact => fact.Item.Kind == SliceOperationKind.Parameter).ToArray();
    }

    private static Dictionary<Fact, int> Traverse(IReadOnlyList<Fact> facts, IReadOnlyCollection<Fact> seeds, SliceDirection direction)
    {
        var result = new Dictionary<Fact, int>();
        var queue = new Queue<Fact>();
        foreach (var seed in seeds) { result[seed] = 0; queue.Enqueue(seed); }
        while (queue.TryDequeue(out var current))
        {
            var adjacent = direction == SliceDirection.Backward
                ? facts.Where(fact => current.Dependencies.Any(edge => edge.Source == fact.Id))
                : facts.Where(fact => fact.Dependencies.Any(edge => edge.Source == current.Id));
            foreach (var next in adjacent)
                if (result.TryAdd(next, result[current] + 1)) queue.Enqueue(next);
        }
        return result;
    }

    private static SourceLocation ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceLocation { StartLine = span.StartLinePosition.Line + 1, StartColumn = span.StartLinePosition.Character + 1,
            EndLine = span.EndLinePosition.Line + 1, EndColumn = span.EndLinePosition.Character + 1 };
    }

    private sealed class Fact(SliceItem item)
    {
        public int Id => Item.Id;
        public SliceItem Item { get; } = item;
        public List<SliceDependency> Dependencies { get; } = [];
    }
}
