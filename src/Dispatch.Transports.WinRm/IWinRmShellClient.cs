using Dispatch.Core.Models;

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
    bool CloseStandardInput = true,
    TimeSpan? ExecutionTimeout = null,
    Action<WinRmShellTransferProgress>? ProgressReporter = null);

public sealed record WinRmShellTransferProgress(
    WinRmShellTransferKind Kind,
    long BytesTransferred,
    long? TotalBytes = null,
    int? FramesTransferred = null,
    int? TotalFrames = null,
    string? TextChunk = null);

public enum WinRmShellTransferKind
{
    Input,
    Output,
    Error
}

public sealed record WinRmShellCommandResult(
    bool Succeeded,
    int? ExitCode,
    string Stdout,
    string Stderr,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool TimedOut = false,
    FailureCategory FailureCategory = FailureCategory.None)
{
    public static WinRmShellCommandResult SucceededResult(
        int? exitCode = 0,
        string stdout = "",
        string stderr = "",
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, exitCode, stdout, stderr, null, metadata);

    public static WinRmShellCommandResult Failed(
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null,
        FailureCategory failureCategory = FailureCategory.ExecutionFailed) =>
        new(false, null, string.Empty, string.Empty, failureMessage, metadata, FailureCategory: failureCategory);

    public static WinRmShellCommandResult TimedOutResult(
        string failureMessage,
        string stdout = "",
        string stderr = "",
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(false, null, stdout, stderr, failureMessage, metadata, TimedOut: true, FailureCategory: FailureCategory.TimedOut);
}
