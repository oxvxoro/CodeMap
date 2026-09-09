using CodeMap.Core.Models;

namespace CodeMap.Engine.Application.Investigation;

public enum CertaintyTier
{
    Heuristic = 0,
    Syntactic = 1,
    Semantic = 2
}

public static class CertaintyTierExtensions
{
    public static CertaintyTier FromResolutionKind(this EdgeResolutionKind resolutionKind) => resolutionKind switch
    {
        EdgeResolutionKind.Semantic => CertaintyTier.Semantic,
        EdgeResolutionKind.Syntactic => CertaintyTier.Syntactic,
        EdgeResolutionKind.Heuristic => CertaintyTier.Heuristic,
        _ => throw new ArgumentOutOfRangeException(nameof(resolutionKind), resolutionKind, null)
    };
}
