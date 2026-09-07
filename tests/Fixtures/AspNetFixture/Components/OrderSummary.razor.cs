using Microsoft.AspNetCore.Components;

namespace AspNetFixture.Components;

public partial class OrderSummary : ComponentBase
{
    [Parameter]
    public decimal Total { get; set; }

    // Not a component parameter — used to assert that attribute binding requires a real
    // [Parameter] symbol, not just a uniquely-named property match (plan §7).
    public string InternalNote { get; set; } = string.Empty;
}
