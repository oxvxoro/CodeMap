namespace AspNetFixture.Services;

// A second IOrderService implementation that is never registered with DI (Program.cs only
// registers OrderService). Exercises the DI-aware flow narrowing (plan §5): flow queries
// starting at IOrderService must not treat this unregistered implementer as reachable via
// the same path as the DI-selected OrderService.
public sealed class LegacyOrderService : IOrderService
{
    public string GetOrder(int id) => $"legacy-order-{id}";
}
