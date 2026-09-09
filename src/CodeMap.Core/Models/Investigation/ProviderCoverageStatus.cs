namespace CodeMap.Core.Models.Investigation;

public enum ProviderCoverageState
{
    Complete,
    Partial,
    NotRun,
    Unsupported,
    Unavailable,
    Error
}

public sealed record ProviderCoverageStatus(
    InvestigationProviderKind Provider,
    ProviderCoverageState State,
    string? Reason,
    int FoundCount)
{
    public static ProviderCoverageStatus Complete(InvestigationProviderKind provider, int count) =>
        new(provider, ProviderCoverageState.Complete, null, count);

    public static ProviderCoverageStatus Partial(InvestigationProviderKind provider, string reason, int count) =>
        new(provider, ProviderCoverageState.Partial, reason, count);

    public static ProviderCoverageStatus NotRun(InvestigationProviderKind provider, string? reason = null) =>
        new(provider, ProviderCoverageState.NotRun, reason, 0);

    public static ProviderCoverageStatus Unsupported(InvestigationProviderKind provider, string reason) =>
        new(provider, ProviderCoverageState.Unsupported, reason, 0);

    public static ProviderCoverageStatus Unavailable(InvestigationProviderKind provider, string reason) =>
        new(provider, ProviderCoverageState.Unavailable, reason, 0);

    public static ProviderCoverageStatus Error(InvestigationProviderKind provider, string reason) =>
        new(provider, ProviderCoverageState.Error, reason, 0);
}
