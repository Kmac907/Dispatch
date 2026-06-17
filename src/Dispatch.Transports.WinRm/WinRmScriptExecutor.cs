using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmScriptExecutor(IWinRmScriptTransferClient scriptTransferClient) : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.WinRm;

    public Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var transferPlan = request.Preparation.TransferPlan;
        var remoteScriptPath = request.Target.PlannedRemoteScriptPath ?? string.Empty;
        var metadata = new Dictionary<string, string>
        {
            ["transport"] = "winrm",
            ["mode"] = "upload-pending",
            ["preparation"] = transferPlan is null ? "missing" : "completed",
            ["plannedRemoteScriptPath"] = remoteScriptPath
        };

        if (transferPlan is not null)
        {
            metadata["transferMode"] = transferPlan.Mode.ToString();
            metadata["scriptByteLength"] = transferPlan.TotalBytes.ToString();
            metadata["scriptSha256"] = transferPlan.ContentSha256;
            metadata["chunkSizeBytes"] = transferPlan.ChunkSizeBytes.ToString();
            metadata["chunkCount"] = transferPlan.ChunkCount.ToString();
        }

        if (transferPlan is null)
        {
            metadata["mode"] = "upload-unavailable";
            return Task.FromResult(new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.PayloadPreparationFailed,
                FailureMessage: "Raw WinRM script upload requires a prepared chunked transfer plan, but none was produced.",
                Metadata: metadata));
        }

        return ExecuteAfterUploadAsync(request, transferPlan, metadata, startedAt, cancellationToken);
    }

    private async Task<TransportScriptExecutionResult> ExecuteAfterUploadAsync(
        TransportScriptExecutionRequest request,
        Dispatch.Core.Execution.ScriptTransferPlan transferPlan,
        Dictionary<string, string> metadata,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var upload = await scriptTransferClient.UploadAsync(
                new WinRmScriptTransferRequest(
                    request.Target.Target.Name,
                    request.Target.PlannedRemoteScriptPath ?? string.Empty,
                    transferPlan),
                cancellationToken)
            .ConfigureAwait(false);

        if (upload.Metadata is not null)
        {
            foreach (var pair in upload.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        if (!upload.Succeeded)
        {
            metadata["mode"] = "upload-failed";
            metadata["uploadStatus"] = "failed";
            return new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: upload.FailureCategory,
                FailureMessage: upload.FailureMessage ?? $"Raw WinRM upload failed for '{request.Target.Target.Name}'.",
                Metadata: metadata);
        }

        metadata["mode"] = "upload-only";
        metadata["uploadStatus"] = "completed";
        return new TransportScriptExecutionResult(
            ExitCode: null,
            Stdout: string.Empty,
            Stderr: string.Empty,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: FailureCategory.ExecutionFailed,
            FailureMessage: "Raw WinRM shell execution is not implemented yet. This slice now uploads the prepared script over raw WinRM, but command execution and artifact collection remain unimplemented.",
            Metadata: metadata);
    }
}
