using Fixture.ProjB;

namespace Fixture.ProjA;

public class SecondCaller
{
    public string CallAgain() => new Greeter().Greet();
}
