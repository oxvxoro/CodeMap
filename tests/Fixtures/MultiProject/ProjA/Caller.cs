using Fixture.ProjB;

namespace Fixture.ProjA;

public class Caller : Greeter
{
    public string Call() => new Greeter().Greet();
}
