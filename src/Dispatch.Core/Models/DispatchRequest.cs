namespace Dispatch.Core.Models;

public sealed record DispatchRequest
{
    public DispatchRequest(
        DispatchPayload payload,
        IReadOnlyList<TargetSpec> targets,
        TransportKind transport,
        IReadOnlyList<int>? expectedExitCodes = null,
        int? throttle = null,
        bool dryRun = false,
        string? localRunRoot = null,
        string? remoteRunRoot = null,
        IReadOnlyList<string>? artifactPaths = null,
        IReadOnlyList<ScriptSecretReference>? scriptSecrets = null,
        ExecutionContextOptions? executionContext = null)
    {
        Payload = payload;
        Targets = targets;
        Transport = transport;
        ExpectedExitCodes = expectedExitCodes ?? [0];
        Throttle = throttle;
        DryRun = dryRun;
        LocalRunRoot = localRunRoot;
        RemoteRunRoot = remoteRunRoot;
        ArtifactPaths = artifactPaths ?? [];
        ScriptSecrets = scriptSecrets ?? [];
        ExecutionContext = executionContext ?? new ExecutionContextOptions();
    }

    public DispatchPayload Payload { get; init; }

    public IReadOnlyList<TargetSpec> Targets { get; init; }

    public TransportKind Transport { get; init; }

    public IReadOnlyList<int> ExpectedExitCodes { get; init; }

    public int? Throttle { get; init; }

    public bool DryRun { get; init; }

    public string? LocalRunRoot { get; init; }

    public string? RemoteRunRoot { get; init; }

    public IReadOnlyList<string> ArtifactPaths { get; init; }

    public IReadOnlyList<ScriptSecretReference> ScriptSecrets { get; init; }

    public ExecutionContextOptions ExecutionContext { get; init; }
}
