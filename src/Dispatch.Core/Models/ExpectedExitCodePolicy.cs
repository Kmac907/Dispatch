namespace Dispatch.Core.Models;

public sealed record ExpectedExitCodePolicy(IReadOnlyList<int> SuccessCodes)
{
    public static ExpectedExitCodePolicy Default { get; } = new([0]);

    public bool IsSuccess(int exitCode) => SuccessCodes.Contains(exitCode);
}
