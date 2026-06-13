namespace Dispatch.Core.Defaults;

public sealed record DispatchDefaults
{
    public const string LocalRunRoot = @"C:\ProgramData\Dispatch\Runs";
    public const string RemoteRunRoot = @"C:\ProgramData\Dispatch\Runs";
    public const int DefaultThrottle = 8;

    public IReadOnlyList<int> ExpectedExitCodes { get; init; } = [0];
}
