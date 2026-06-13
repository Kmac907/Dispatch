using Dispatch.Core.Models;
using Microsoft.Extensions.Logging;

namespace Dispatch.Core.Execution;

internal sealed class NotImplementedDispatchPlanner(ILogger<NotImplementedDispatchPlanner> logger) : IDispatchPlanner
{
    public Task<ExecutionPlan> CreatePlanAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        logger.LogDebug("Dispatch planning was requested before the planning slice is implemented.");
        throw new NotSupportedException("Dispatch request planning is not implemented yet.");
    }
}
