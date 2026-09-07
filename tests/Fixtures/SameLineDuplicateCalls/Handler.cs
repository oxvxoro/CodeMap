namespace Fixture;

public class Repository
{
    public void Save() { }
}

public class Handler
{
    private readonly Repository repository = new();

    public void Run() { repository.Save(); repository.Save(); }
}
