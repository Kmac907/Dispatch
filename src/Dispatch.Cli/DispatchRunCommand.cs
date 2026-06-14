using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed record DispatchRunCommand(
    bool DryRun,
    string ScriptPath,
    IReadOnlyList<string> ScriptArguments,
    TargetSpec Target,
    TransportKind Transport,
    IReadOnlyList<int> ExpectedExitCodes,
    int? Throttle,
    string? LocalRunRoot,
    string? RemoteRunRoot,
    bool RunAsSystem)
{
    public DispatchRequest ToRequest() =>
        new(
            payload: new ScriptPayload(ScriptPath, ScriptArguments),
            targets: [Target],
            transport: Transport,
            expectedExitCodes: ExpectedExitCodes,
            throttle: Throttle,
            dryRun: DryRun,
            localRunRoot: LocalRunRoot,
            remoteRunRoot: RemoteRunRoot,
            executionContext: new ExecutionContextOptions(RunAsSystem));
}
