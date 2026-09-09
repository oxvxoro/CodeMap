namespace CodeMap.Core.Models;

/// <summary>One bounded graph result page and whether another page is available.</summary>
public readonly record struct RelationPage<T>(IReadOnlyList<T> Items, bool HasMore);
