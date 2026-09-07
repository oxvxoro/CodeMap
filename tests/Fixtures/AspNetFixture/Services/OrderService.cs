namespace AspNetFixture.Services;

public sealed class OrderService : IOrderService
{
    public string GetOrder(int id) => $"order-{id}";
}
