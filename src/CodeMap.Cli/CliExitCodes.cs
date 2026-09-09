namespace CodeMap.Cli;

/// <summary>Stable process exit meanings shared by command handlers and tests.</summary>
public static class CliExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NoMatchOrAmbiguous = 2;
    public const int Cancelled = 130;
}
