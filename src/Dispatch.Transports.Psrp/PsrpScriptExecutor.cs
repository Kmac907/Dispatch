using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.Psrp;

public sealed class PsrpScriptExecutor : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.Psrp;

    public Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var endedAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transport"] = "psrp",
            ["mode"] = "not-implemented",
            ["payloadType"] = request.Plan.Job.Payload.PayloadType.ToString().ToLowerInvariant(),
            ["executionStatus"] = "failed"
        };

        return Task.FromResult(new TransportScriptExecutionResult(
            ExitCode: null,
            Stdout: string.Empty,
            Stderr: string.Empty,
            StartedAt: startedAt,
            EndedAt: endedAt,
            FailureCategory: FailureCategory.TransportUnavailable,
            FailureMessage: "PSRP execution is not implemented in this slice.",
            Metadata: metadata));
    }
}
