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
        var transferPlan = request.Preparation.TransferPlan;
        var metadata = new Dictionary<string, string>
        {
            ["transport"] = "winrm",
            ["mode"] = "prepared-only",
            ["preparation"] = transferPlan is null ? "missing" : "completed",
            ["plannedRemoteScriptPath"] = request.Target.PlannedRemoteScriptPath ?? string.Empty
        };

        if (transferPlan is not null)
        {
            metadata["transferMode"] = transferPlan.Mode.ToString();
            metadata["scriptByteLength"] = transferPlan.TotalBytes.ToString();
            metadata["scriptSha256"] = transferPlan.ContentSha256;
            metadata["chunkSizeBytes"] = transferPlan.ChunkSizeBytes.ToString();
            metadata["chunkCount"] = transferPlan.ChunkCount.ToString();
        }

        return Task.FromResult(new TransportScriptExecutionResult(
            ExitCode: null,
            Stdout: string.Empty,
            Stderr: string.Empty,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: FailureCategory.ExecutionFailed,
            FailureMessage: "Raw WinRM shell execution is not implemented yet. This slice validates WinRM reachability and prepares chunked script transfer planning only.",
            Metadata: metadata));
    }
}
