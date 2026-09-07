using Microsoft.AspNetCore.Components;

namespace AspNetFixture.Components;

public partial class OrderForm : ComponentBase
{
    [Parameter]
    public decimal OrderTotal { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public void Submit()
    {
        CustomerName = CustomerName.Trim();
    }
}
