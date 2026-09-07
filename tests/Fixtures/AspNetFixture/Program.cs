using AspNetFixture.Services;

var builder = WebApplication.CreateBuilder(args);

// Supported DI registrations: <TService, TImplementation> and <TService> generic overloads.
builder.Services.AddSingleton<IOrderService, OrderService>();
builder.Services.AddScoped<OrderNotifier>();

// Unsupported DI registration: factory delegate overload — must not produce a
// DependencyRegistration node (plan §3.3).
builder.Services.AddTransient<OrderNotifier>(_ => new OrderNotifier());

var app = builder.Build();

// Method-group handler: resolves to Handlers.GetOrderHandler and gets a RoutesTo edge.
app.MapGet("/orders/{id}", AspNetFixture.Handlers.GetOrderHandler);

// Lambda handler: gets a RoutesTo edge to a NodeKind.Function node for the lambda itself
// (plan §5.7), and the call inside the lambda body is attributed to that lambda node, not
// to any enclosing method, so flow can reach it end-to-end (plan §9.4).
app.MapPost("/orders", (AspNetFixture.Services.IOrderService orders) => orders.GetOrder(0));

// MapMethods with a constant verb array.
app.MapMethods("/orders/{id}/status", new[] { "GET", "HEAD" }, AspNetFixture.Handlers.GetOrderHandler);

// Overloaded method-group handler: a bare method-group argument is ambiguous here (CS1503),
// so the call site pins the overload with an explicit delegate conversion — Roslyn still
// resolves this to a real, single IMethodSymbol (the parameterless overload), the same shape
// AnalyzeMinimalApi sees for any other method-group handler. The RoutesTo edge must resolve
// to exactly that overload via exact symbol identity (plan §5.6) — name/qualified-name
// matching alone cannot disambiguate since both overloads share the same declaring type and
// method name.
app.MapGet("/ping", (Func<string>)AspNetFixture.Handlers.PingHandler);

// Non-constant route template — must not produce a Route node.
var dynamicSegment = Environment.GetEnvironmentVariable("SEGMENT") ?? "dynamic";
app.MapGet("/" + dynamicSegment, () => "no route node expected");

app.Run();
