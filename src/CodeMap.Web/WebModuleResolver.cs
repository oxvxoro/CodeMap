using System.Text.Json;

namespace CodeMap.Web;

internal static class WebModuleResolver
{
    internal sealed record AliasMap(string Root, string BaseUrl, IReadOnlyList<(string Prefix, string Target)> Paths);

    internal static AliasMap Load(string root)
    {
        var configPath = FindConfigPath(root);
        if (configPath is null)
            return new AliasMap(root, root, []);

        var configDir = Path.GetDirectoryName(configPath)!;
        try
        {






            using var document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var compiler = document.RootElement.TryGetProperty("compilerOptions", out var options) ? options : default;
            var baseUrl = compiler.ValueKind == JsonValueKind.Object && compiler.TryGetProperty("baseUrl", out var baseUrlElement)
                ? Path.GetFullPath(Path.Combine(configDir, baseUrlElement.GetString() ?? "."))
                : configDir;
            var paths = new List<(string Prefix, string Target)>();
            if (compiler.ValueKind == JsonValueKind.Object && compiler.TryGetProperty("paths", out var pathsElement))
            {
                foreach (var property in pathsElement.EnumerateObject())
                {
                    var prefix = property.Name.TrimEnd('*');
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            var target = item.GetString();
                            if (!string.IsNullOrWhiteSpace(target))
                                paths.Add((prefix, target.TrimEnd('*')));
                        }
                    }
                    else
                    {
                        var target = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(target))
                            paths.Add((prefix, target.TrimEnd('*')));
                    }
                }
            }
            paths.Sort((left, right) => right.Prefix.Length.CompareTo(left.Prefix.Length));
            return new AliasMap(configDir, baseUrl, paths);
        }
        catch (JsonException)
        {
            return new AliasMap(configDir, configDir, []);
        }
    }

    private static string? FindConfigPath(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            foreach (var name in new[] { "tsconfig.json", "jsconfig.json" })
            {
                var candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    internal static string? Resolve(string currentFile, string specifier, IEnumerable<string> files, AliasMap? aliases)
    {
        if (specifier.StartsWith('.'))
            return ResolveRelative(currentFile, specifier, files);

        if (aliases is not null)
        {
            foreach (var (prefix, target) in aliases.Paths)
            {
                if (!specifier.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var remainder = specifier[prefix.Length..];
                var mapped = Path.GetFullPath(Path.Combine(aliases.BaseUrl, target.Replace('/', Path.DirectorySeparatorChar), remainder.Replace('/', Path.DirectorySeparatorChar)));
                var resolved = MatchFile(mapped, files);
                if (resolved is not null)
                    return resolved;
            }
        }

        return null;
    }

    private static string? ResolveRelative(string current, string specifier, IEnumerable<string> files)
    {
        var basePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, specifier));
        return MatchFile(basePath, files);
    }

    private static string? MatchFile(string basePath, IEnumerable<string> files)
    {
        var fileSet = files as IReadOnlySet<string>
            ?? files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string> { basePath };
        foreach (var extension in new[] { ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx" })
            candidates.Add(basePath + extension);
        foreach (var indexName in new[] { "index.js", "index.ts", "index.tsx", "index.jsx" })
            candidates.Add(Path.Combine(basePath, indexName));

        foreach (var candidate in candidates.Select(Path.GetFullPath))
        {
            if (fileSet.Contains(candidate))
                return candidate;
        }
        return null;
    }
}
