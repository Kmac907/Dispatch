namespace Dispatch.Transports.WinRm;

public interface IWinRmShellClient
{
    Task<WinRmShellCommandResult> ExecuteAsync(
        WinRmShellCommandRequest request,
        CancellationToken cancellationToken);
}

public sealed record WinRmShellCommandRequest(
    string Target,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<byte[]> StandardInputFrames,
    bool CloseStandardInput = true);

public sealed record WinRmShellCommandResult(
    bool Succeeded,
    int? ExitCode,
    string Stdout,
    string Stderr,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static WinRmShellCommandResult Failed(
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(false, null, string.Empty, string.Empty, failureMessage, metadata);
}
