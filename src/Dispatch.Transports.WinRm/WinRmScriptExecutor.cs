using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmScriptExecutor(
    IWinRmScriptTransferClient scriptTransferClient,
    IWinRmShellClient shellClient) : ITransportScriptExecutor
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
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        var command = ResolveCommand(request);
        if (command is null)
        {
            metadata["mode"] = "execution-unavailable";
            return Task.FromResult(new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.PayloadPreparationFailed,
                FailureMessage: $"Raw WinRM execution requires a prepared PowerShell command for '{request.Target.Target.Name}'.",
                Metadata: metadata));
        }

        metadata["executionCommand"] = command.RenderedCommand;
        return ExecuteAfterUploadAsync(request, transferPlan, command, metadata, startedAt, cancellationToken);
    }

    private async Task<TransportScriptExecutionResult> ExecuteAfterUploadAsync(
        TransportScriptExecutionRequest request,
        Dispatch.Core.Execution.ScriptTransferPlan transferPlan,
        DirectExecutionCommand command,
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

        metadata["mode"] = "execution-pending";
        metadata["uploadStatus"] = "completed";

        var shellResult = await shellClient.ExecuteAsync(
                new WinRmShellCommandRequest(
                    request.Target.Target.Name,
                    command.Executable,
                    command.Arguments,
                    []),
                cancellationToken)
            .ConfigureAwait(false);

        if (shellResult.Metadata is not null)
        {
            foreach (var pair in shellResult.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        metadata["executable"] = command.Executable;

        if (!shellResult.Succeeded)
        {
            metadata["mode"] = "execution-failed";
            metadata["executionStatus"] = "failed";
            return new TransportScriptExecutionResult(
                ExitCode: shellResult.ExitCode,
                Stdout: shellResult.Stdout,
                Stderr: shellResult.Stderr,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.ExecutionFailed,
                FailureMessage: shellResult.FailureMessage ?? $"Raw WinRM shell execution failed for '{request.Target.Target.Name}'.",
                Metadata: metadata);
        }

        var expectedExitCodes = request.Plan.Job.ExpectedExitCodes;
        var succeeded = shellResult.ExitCode is not null && expectedExitCodes.Contains(shellResult.ExitCode.Value);
        metadata["mode"] = "executed";
        metadata["executionStatus"] = "completed";

        return new TransportScriptExecutionResult(
            ExitCode: shellResult.ExitCode,
            Stdout: shellResult.Stdout,
            Stderr: shellResult.Stderr,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: succeeded ? FailureCategory.None : FailureCategory.UnexpectedExitCode,
            FailureMessage: succeeded
                ? null
                : $"Raw WinRM exited with code {shellResult.ExitCode}; expected {string.Join(", ", expectedExitCodes)}.",
            Metadata: metadata);
    }

    private static DirectExecutionCommand? ResolveCommand(TransportScriptExecutionRequest request)
    {
        if (request.Target.PlannedCommand is not null)
        {
            return request.Target.PlannedCommand;
        }

        if (request.Plan.Job.Payload is not ScriptPayload script
            || string.IsNullOrWhiteSpace(request.Preparation.RemoteScriptPath))
        {
            return null;
        }

        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            request.Preparation.RemoteScriptPath
        };

        arguments.AddRange(script.ScriptArguments);
        return new DirectExecutionCommand("powershell.exe", arguments);
    }
}
