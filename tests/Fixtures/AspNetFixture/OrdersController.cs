using Microsoft.AspNetCore.Mvc;
using AspNetFixture.Services;

namespace AspNetFixture;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;

    public OrdersController(IOrderService orders) => _orders = orders;

    [HttpGet("{id}")]
    public string Get(int id) => _orders.GetOrder(id);

    [HttpPost]
    public string Create() => _orders.GetOrder(0);

    // No HTTP verb attribute, only [Route] -> ANY.
    [Route("ping")]
    public string Ping() => "pong";
}
