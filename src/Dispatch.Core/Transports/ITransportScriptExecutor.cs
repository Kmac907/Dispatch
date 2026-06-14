using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public interface ITransportScriptExecutor
{
    TransportKind Kind { get; }

    Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken);
}
