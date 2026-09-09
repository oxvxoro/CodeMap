using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application.Investigation;

public enum CostClass
{
    LocalSlice,
    GraphRow,
    SourceIo
}

/// <summary>Bounded retrieval policy for one investigation provider.</summary>
public sealed record ProviderPlan(
    InvestigationProviderKind Kind,
    int InitialWindow,
    int ExpansionWindow,
    int MaxWindow,
    int Priority,
    CostClass CostClass)
{
    public static ProviderPlan For(InvestigationProviderKind kind) =>
        kind == InvestigationProviderKind.LocalSlice
            ? new(kind, 8, 8, 32, 0, CostClass.LocalSlice)
            : new(kind, 8, 16, 64, 10, CostClass.GraphRow);
}

internal sealed record InvestigationProviderPlan(
    IInvestigationProvider Provider,
    ProviderPlan Policy);
