namespace Dispatch.Core.Defaults;

public sealed record DispatchDefaults
{
    public const string GlobalConfigPath = @"C:\ProgramData\Dispatch\config.yml";
    public const string LocalRunRoot = @"C:\ProgramData\Dispatch\Runs";
    public const string RemoteRunRoot = @"C:\ProgramData\Dispatch\Runs";
    public const string CredentialStorePath = @"C:\ProgramData\Dispatch\Credentials\references.json";
    public const int DefaultThrottle = 8;

    public IReadOnlyList<int> ExpectedExitCodes { get; init; } = [0];
}
