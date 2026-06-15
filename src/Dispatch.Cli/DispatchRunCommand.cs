using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed record DispatchRunCommand(
    bool DryRun,
    string ScriptPath,
    IReadOnlyList<string> ScriptArguments,
    IReadOnlyList<TargetSpec> Targets,
    TransportKind Transport,
    IReadOnlyList<int> ExpectedExitCodes,
    int? Throttle,
    string? LocalRunRoot,
    string? RemoteRunRoot,
    IReadOnlyList<string> ArtifactPaths,
    bool RunAsSystem,
    bool NoDashboard,
    DispatchOutputMode OutputMode)
{
    public DispatchRequest ToRequest() =>
        new(
            payload: new ScriptPayload(ScriptPath, ScriptArguments),
            targets: Targets,
            transport: Transport,
            expectedExitCodes: ExpectedExitCodes,
            throttle: Throttle,
            dryRun: DryRun,
            localRunRoot: LocalRunRoot,
            remoteRunRoot: RemoteRunRoot,
            artifactPaths: ArtifactPaths,
            executionContext: new ExecutionContextOptions(RunAsSystem));
}
