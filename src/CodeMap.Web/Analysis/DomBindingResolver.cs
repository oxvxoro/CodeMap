namespace CodeMap.Web;

/// <summary>Shared DOM selector normalization for HTML/script binding stages.</summary>
public static class DomBindingResolver
{
    public static string NormalizeSelector(string selector) =>
        string.IsNullOrWhiteSpace(selector) ? string.Empty : selector.Trim();
}
