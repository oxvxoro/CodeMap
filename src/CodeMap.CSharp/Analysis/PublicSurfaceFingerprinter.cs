using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class PublicSurfaceFingerprinter
{
    public static string Compute(IEnumerable<CodeNode> declarations)
    {
        var material = string.Join("\n", declarations
            .Where(node => string.Equals(node.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => $"{node.Kind}|{node.QualifiedName}|{node.Signature}|{node.Visibility}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
