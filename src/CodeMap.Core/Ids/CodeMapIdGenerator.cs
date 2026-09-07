namespace CodeMap.Core.Ids;

public sealed class CodeMapIdGenerator : ICodeMapIdGenerator
{
    public string CreateFileId(string projectName, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return $"file://{NormalizeSegment(projectName)}/{NormalizePath(relativePath)}";
    }

    public string CreateSymbolId(string projectName, string qualifiedSymbolSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedSymbolSignature);

        return $"csharp://{NormalizeSegment(projectName)}/{NormalizeSegment(qualifiedSymbolSignature)}";
    }

    public string CreateGlobalSymbolId(string documentationCommentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationCommentId);

        return $"sym://{NormalizeSegment(documentationCommentId)}";
    }

    private static string NormalizeSegment(string value) =>
        value.Replace('\\', '/').Trim('/');

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
