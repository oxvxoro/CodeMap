namespace CodeMap.Engine.Application.Investigation;

internal interface IFileTextAccessor
{
    string? Read(string relativePath, int startLine, int endLine);
}

internal sealed class FileTextAccessor(string root) : IFileTextAccessor
{
    public string? Read(string relativePath, int startLine, int endLine)
    {
        var rootPath = Path.GetFullPath(root);
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(path))
            return null;
        var lines = File.ReadAllLines(path);
        var start = Math.Max(1, startLine);
        var end = Math.Min(lines.Length, Math.Max(start, endLine));
        return start > end ? null : string.Join(Environment.NewLine, lines[(start - 1)..end]);
    }
}
