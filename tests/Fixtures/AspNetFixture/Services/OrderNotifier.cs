namespace AspNetFixture.Services;

/// <summary>A second, unrelated service registered with its own lifetime (no interface split) —
/// exercises the single-type-argument AddScoped/AddTransient overload.</summary>
public sealed class OrderNotifier
{
    public void Notify(string message) => System.Console.WriteLine(message);
}
