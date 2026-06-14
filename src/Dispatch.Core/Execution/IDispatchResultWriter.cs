using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IDispatchResultWriter
{
    Task WriteAsync(ExecutionPlan plan, DispatchRunResult result, CancellationToken cancellationToken);
}
