namespace Dispatch.Core.Models;

public sealed record TargetExecutionResult(
    string RunId,
    string Target,
    TransportKind Transport,
    PayloadKind PayloadType,
    string PayloadName,
    TargetExecutionState State,
    int? ExitCode,
    IReadOnlyList<int> ExpectedExitCodes,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    FailureCategory FailureCategory,
    string? FailureMessage,
    string? StdoutPath = null,
    string? StderrPath = null,
    string ResultPath = "",
    IReadOnlyList<string>? Artifacts = null,
    string? ArtifactCollectionStatus = null,
    string? ArtifactCollectionFailureMessage = null,
    string? SecretHandoffStatus = null,
    string? CleanupStatus = null,
    IReadOnlyDictionary<string, string>? TransportMetadata = null,
    IReadOnlyList<PowerShellStreamRecord>? StreamRecords = null)
{
    public long DurationMs => Convert.ToInt64((EndedAt - StartedAt).TotalMilliseconds);
}
