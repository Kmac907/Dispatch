namespace Dispatch.Core.Models;

public sealed record DispatchJob
{
    public DispatchJob(
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
        ResultPolicy ResultPolicy,
        IReadOnlyList<ScriptSecretHandoffPlan>? ScriptSecrets = null)
    {
        this.RunId = RunId;
        this.Targets = Targets;
        this.Payload = Payload;
        this.Transport = Transport;
        this.ExecutionContext = ExecutionContext;
        this.ScriptTransferPolicy = ScriptTransferPolicy;
        this.TimeoutPolicy = TimeoutPolicy;
        this.RetryPolicy = RetryPolicy;
        this.ExpectedExitCodes = ExpectedExitCodes;
        this.ArtifactPolicy = ArtifactPolicy;
        this.ResultPolicy = ResultPolicy;
        this.ScriptSecrets = ScriptSecrets ?? [];
    }

    public string RunId { get; init; }

    public IReadOnlyList<TargetSpec> Targets { get; init; }

    public DispatchPayload Payload { get; init; }

    public TransportKind Transport { get; init; }

    public ExecutionContextOptions ExecutionContext { get; init; }

    public ScriptTransferPolicy ScriptTransferPolicy { get; init; }

    public TimeoutPolicy TimeoutPolicy { get; init; }

    public RetryPolicy RetryPolicy { get; init; }

    public IReadOnlyList<int> ExpectedExitCodes { get; init; }

    public ArtifactPolicy ArtifactPolicy { get; init; }

    public ResultPolicy ResultPolicy { get; init; }

    public IReadOnlyList<ScriptSecretHandoffPlan> ScriptSecrets { get; init; }
}
