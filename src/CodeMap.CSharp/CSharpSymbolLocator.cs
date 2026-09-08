using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.CSharp;

public sealed class CSharpSymbolLocator
{
    public async Task<IMethodSymbol?> ResolveAsync(
        Project project,
        string codeMapId,
        string relativePath,
        int? startLine,
        string name,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return null;

        if (codeMapId.StartsWith("sym://", StringComparison.Ordinal))
        {
            var documentationId = codeMapId["sym://".Length..];
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationId, compilation) is IMethodSymbol documented)
                return documented;
        }

        var projectRoot = Path.GetDirectoryName(project.FilePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(projectRoot) || startLine is null)
            return null;
        var expectedPath = relativePath.Replace('\\', '/');
        var candidates = new List<IMethodSymbol>();
        foreach (var document in project.Documents.Where(document => document.FilePath is not null))
        {
            var documentRelativePath = Path.GetRelativePath(projectRoot, document.FilePath!).Replace('\\', '/');
            if (!string.Equals(documentRelativePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model is null)
                continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
                continue;
            foreach (var syntax in root.DescendantNodes().Where(IsExecutableDeclaration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1 != startLine)
                    continue;
                var symbol = GetSymbol(model, syntax, cancellationToken);
                if (symbol is not null && string.Equals(symbol.Name, name, StringComparison.Ordinal))
                    candidates.Add(symbol);
            }
        }
        return candidates.Select(symbol => (ISymbol)symbol).Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().SingleOrDefault();
    }

    private static bool IsExecutableDeclaration(SyntaxNode syntax) => syntax is BaseMethodDeclarationSyntax
        or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax;

    private static IMethodSymbol? GetSymbol(SemanticModel model, SyntaxNode syntax, CancellationToken cancellationToken) => syntax switch
    {
        AnonymousFunctionExpressionSyntax anonymous => model.GetSymbolInfo(anonymous, cancellationToken).Symbol as IMethodSymbol,
        _ => model.GetDeclaredSymbol(syntax, cancellationToken) as IMethodSymbol
    };
}
