using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.Psrp;

public sealed class PsrpScriptExecutor(IPsrpCommandClient commandClient) : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.Psrp;

    public async Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transport"] = "psrp",
            ["mode"] = "execution-pending",
            ["payloadType"] = request.Plan.Job.Payload.PayloadType.ToString().ToLowerInvariant(),
            ["executionStatus"] = "pending"
        };

        if (request.Plan.Job.Payload is not CommandPayload commandPayload)
        {
            metadata["mode"] = "script-not-implemented";
            metadata["executionStatus"] = "failed";
            return new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.TransportUnavailable,
                FailureMessage: "PSRP script execution is not implemented in this slice.",
                Metadata: metadata);
        }

        if (request.Target.PlannedCommand is null)
        {
            metadata["mode"] = "execution-unavailable";
            metadata["executionStatus"] = "failed";
            return new TransportScriptExecutionResult(
                ExitCode: null,
                Stdout: string.Empty,
                Stderr: string.Empty,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: FailureCategory.PayloadPreparationFailed,
                FailureMessage: $"PSRP command execution requires a prepared command for '{request.Target.Target.Name}'.",
                Metadata: metadata);
        }

        metadata["commandShell"] = commandPayload.Shell;
        if (!string.IsNullOrWhiteSpace(commandPayload.WorkingDirectory))
        {
            metadata["workingDirectory"] = commandPayload.WorkingDirectory;
        }

        metadata["executionCommand"] = request.Target.PlannedCommand.RenderedCommand;

        var result = await commandClient.ExecuteAsync(
            new PsrpCommandRequest(
                request.Target.Target.Name,
                request.Target.PlannedCommand.Executable,
                RenderArguments(request.Target.PlannedCommand.Arguments),
                commandPayload.WorkingDirectory,
                request.Plan.Job.TimeoutPolicy.ExecutionTimeout),
            cancellationToken).ConfigureAwait(false);

        if (result.Metadata is not null)
        {
            foreach (var pair in result.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        metadata["executable"] = request.Target.PlannedCommand.Executable;

        if (!result.Succeeded)
        {
            metadata["mode"] = "execution-failed";
            metadata["executionStatus"] = "failed";
            metadata["failureCategory"] = result.FailureCategory.ToString();
            return new TransportScriptExecutionResult(
                ExitCode: result.ExitCode,
                Stdout: result.Stdout,
                Stderr: result.Stderr,
                StartedAt: startedAt,
                EndedAt: DateTimeOffset.UtcNow,
                FailureCategory: result.FailureCategory,
                FailureMessage: result.FailureMessage,
                Metadata: metadata);
        }

        var succeeded = result.ExitCode is not null && request.Plan.Job.ExpectedExitCodes.Contains(result.ExitCode.Value);
        metadata["mode"] = "executed";
        metadata["executionStatus"] = "completed";

        return new TransportScriptExecutionResult(
            ExitCode: result.ExitCode,
            Stdout: result.Stdout,
            Stderr: result.Stderr,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: succeeded ? FailureCategory.None : FailureCategory.UnexpectedExitCode,
            FailureMessage: succeeded
                ? null
                : $"PSRP command exited with code {result.ExitCode}; expected {string.Join(", ", request.Plan.Job.ExpectedExitCodes)}.",
            Metadata: metadata);
    }

    private static string RenderArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
