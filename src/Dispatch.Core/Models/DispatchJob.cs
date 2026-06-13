namespace Dispatch.Core.Models;

public sealed record DispatchJob(
    string RunId,
    IReadOnlyList<TargetSpec> Targets,
    DispatchPayload Payload,
    TransportKind Transport,
    ExecutionContextOptions ExecutionContext,
    ScriptTransferPolicy ScriptTransferPolicy,
    TimeoutPolicy TimeoutPolicy,
    RetryPolicy RetryPolicy,
    IReadOnlyList<int> ExpectedExitCodes,
    ArtifactPolicy ArtifactPolicy,
    ResultPolicy ResultPolicy);
