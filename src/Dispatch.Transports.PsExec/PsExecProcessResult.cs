using Dispatch.Core.Models;

namespace Dispatch.Transports.PsExec;

public sealed record PsExecProcessResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null);
