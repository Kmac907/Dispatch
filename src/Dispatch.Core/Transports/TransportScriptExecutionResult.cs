using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public sealed record TransportScriptExecutionResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata = null);
