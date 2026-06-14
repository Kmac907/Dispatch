using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IDispatchExecutor
{
    Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken);

    Task<DispatchRunResult> ExecuteAsync(
        ExecutionPlan plan,
        IDispatchExecutionObserver observer,
        CancellationToken cancellationToken);
}
