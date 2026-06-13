using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IDispatchPlanner
{
    Task<ExecutionPlan> CreatePlanAsync(DispatchRequest request, CancellationToken cancellationToken);
}
