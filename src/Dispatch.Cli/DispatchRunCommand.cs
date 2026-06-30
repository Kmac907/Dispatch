using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed record DispatchRunCommand(
    bool DryRun,
    DispatchPayload Payload,
    IReadOnlyList<TargetSpec> Targets,
    TransportKind Transport,
    string? ConfigPath,
    IReadOnlyList<int> ExpectedExitCodes,
    int? Throttle,
    string? LocalRunRoot,
    string? RemoteRunRoot,
    IReadOnlyList<string> ArtifactPaths,
    IReadOnlyList<ScriptSecretReference> ScriptSecrets,
    string? CredentialReference,
    bool RunAsSystem,
    bool AllowRunAsSystem,
    bool NoDashboard,
    DispatchOutputMode OutputMode,
    bool NoColor,
    bool Quiet,
    bool Verbose,
    bool Trace)
{
    public DispatchRequest ToRequest() =>
        new(
            payload: Payload,
            targets: string.IsNullOrWhiteSpace(CredentialReference)
                ? Targets
                : Targets.Select(target => target with { CredentialReference = CredentialReference }).ToArray(),
            transport: Transport,
            expectedExitCodes: ExpectedExitCodes,
            throttle: Throttle,
            dryRun: DryRun,
            localRunRoot: LocalRunRoot,
            remoteRunRoot: RemoteRunRoot,
            artifactPaths: ArtifactPaths,
            scriptSecrets: ScriptSecrets,
            executionContext: new ExecutionContextOptions(RunAsSystem));
}
