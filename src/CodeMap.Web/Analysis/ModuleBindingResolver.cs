namespace CodeMap.Web;

/// <summary>Normalizes module specifiers before the language analyzer resolves them.</summary>
public static class ModuleBindingResolver
{
    public static string Normalize(string specifier) =>
        string.IsNullOrWhiteSpace(specifier) ? string.Empty : specifier.Trim().Replace('\\', '/');
}
