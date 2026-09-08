using CodeMap.CSharp;
using CodeMap.Core;
using CodeMap.Web;

namespace CodeMap.Storage;

internal enum SourceOwner
{
    Unowned,
    CSharp,
    Web,
    Ignored,
    OutsideRoot
}

internal sealed class SourceOwnershipResolver
{
    private readonly string _root;
    private readonly CSharpLanguageAnalyzer _csharp = new();
    private readonly WebLanguageAnalyzer _web = new();

    public SourceOwnershipResolver(string repositoryRoot) => _root = Path.GetFullPath(repositoryRoot);

    public SourceOwner GetOwner(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (IgnoreRules.IsOutsideRoot(_root, fullPath))
            return SourceOwner.OutsideRoot;
        if (IgnoreRules.IsIgnored(_root, fullPath))
            return SourceOwner.Ignored;
        if (_csharp.CanAnalyze(fullPath) || DotNetMarkupFiles.IsMarkupFile(fullPath))
            return SourceOwner.CSharp;
        return _web.CanAnalyze(fullPath) ? SourceOwner.Web : SourceOwner.Unowned;
    }
}
