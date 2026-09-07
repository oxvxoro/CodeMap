namespace CodeMap.Core;


public static class IgnoreRules
{
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".codemap", "bin", "obj", "node_modules", "dist", "build", "coverage"
    };


    public static bool IsIgnored(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IgnoredSegments.Contains);
    }


    public static bool IsOutsideRoot(string root, string file)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file));
        return relative.StartsWith("..", StringComparison.Ordinal);
    }
}
