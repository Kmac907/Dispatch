using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IScriptPreparationService
{
    Task<ScriptPreparationResult> PrepareAsync(ExecutionPlan plan, CancellationToken cancellationToken);
}
