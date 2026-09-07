using Microsoft.AspNetCore.Http;
using AspNetFixture.Services;

namespace AspNetFixture;

public static class Handlers
{
    public static string GetOrderHandler(IOrderService orders, int id) => orders.GetOrder(id);

    public static IResult CreateOrderHandler(IOrderService orders) => Results.Ok(orders.GetOrder(0));

    // Overloaded method-group handler: two source methods share the name "PingHandler".
    // Roslyn selects the parameterless overload as the route delegate target; the RoutesTo
    // edge must resolve to that exact overload via symbol identity, not the other one.
    public static string PingHandler() => "pong";

    public static string PingHandler(IOrderService orders) => orders.GetOrder(0);
}
