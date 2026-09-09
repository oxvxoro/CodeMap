namespace CodeMap.Core.Models.Investigation;

public sealed record InvestigationCoverage(
    IReadOnlyList<ProviderCoverageStatus> Providers,
    IReadOnlyDictionary<string, int> Remaining,
    IReadOnlyList<string> NegativeEvidence)
{
    public static InvestigationCoverage Empty { get; } = new(
        Array.Empty<ProviderCoverageStatus>(),
        new Dictionary<string, int>(StringComparer.Ordinal),
        Array.Empty<string>());
}
