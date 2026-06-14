using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public sealed record TransportScriptExecutionRequest(
    ExecutionPlan Plan,
    TargetExecution Target,
    TargetScriptPreparationResult Preparation);
