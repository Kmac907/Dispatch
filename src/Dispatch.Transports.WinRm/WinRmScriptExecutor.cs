using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmScriptExecutor : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.WinRm;

    public Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>
        {
            ["transport"] = "winrm",
            ["mode"] = "probe-only",
            ["plannedRemoteScriptPath"] = request.Target.PlannedRemoteScriptPath ?? string.Empty
        };

        return Task.FromResult(new TransportScriptExecutionResult(
            ExitCode: null,
            Stdout: string.Empty,
            Stderr: string.Empty,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: FailureCategory.ExecutionFailed,
            FailureMessage: "Raw WinRM shell execution is not implemented yet. This slice validates WinRM reachability and planning only.",
            Metadata: metadata));
    }
}
