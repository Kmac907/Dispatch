using Dispatch.Core.Models;
using Microsoft.Extensions.Logging;

namespace Dispatch.Core.Execution;

internal sealed class NotImplementedDispatchExecutor(ILogger<NotImplementedDispatchExecutor> logger) : IDispatchExecutor
{
    public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        logger.LogDebug("Dispatch execution was requested before the executor slice is implemented.");
        throw new NotSupportedException("Dispatch execution is not implemented yet.");
    }
}
