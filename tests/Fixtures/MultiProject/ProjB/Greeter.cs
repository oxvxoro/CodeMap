namespace Fixture.ProjB;

public interface IGreeter
{
    string GetGreeting();
}

public class Greeter : IGreeter
{
    public string Greet() => GetGreeting();

    public string GetGreeting() => "hi";
}

public class AlternateGreeter : IGreeter
{
    public string GetGreeting() => "alternate";
}
