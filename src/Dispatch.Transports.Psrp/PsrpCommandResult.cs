using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public sealed record PsrpCommandResult(
    bool Succeeded,
    int? ExitCode,
    string Stdout,
    string Stderr,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<PowerShellStreamRecord>? StreamRecords = null)
{
    public static PsrpCommandResult Success(
        int exitCode,
        string stdout,
        string stderr,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<PowerShellStreamRecord>? streamRecords = null) =>
        new(true, exitCode, stdout, stderr, Metadata: metadata, StreamRecords: streamRecords);

    public static PsrpCommandResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<PowerShellStreamRecord>? streamRecords = null,
        int? exitCode = null,
        string stdout = "",
        string stderr = "") =>
        new(false, exitCode, stdout, stderr, failureCategory, failureMessage, metadata, streamRecords);
}
