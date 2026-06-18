using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public sealed record PsrpCommandResult(
    bool Succeeded,
    int? ExitCode,
    string Stdout,
    string Stderr,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static PsrpCommandResult Success(
        int exitCode,
        string stdout,
        string stderr,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, exitCode, stdout, stderr, Metadata: metadata);

    public static PsrpCommandResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null,
        int? exitCode = null,
        string stdout = "",
        string stderr = "") =>
        new(false, exitCode, stdout, stderr, failureCategory, failureMessage, metadata);
}
