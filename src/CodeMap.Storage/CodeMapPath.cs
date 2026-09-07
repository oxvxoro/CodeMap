namespace CodeMap.Storage;

public static class CodeMapPath
{
    public static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}
