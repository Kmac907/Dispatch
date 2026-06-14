namespace Dispatch.Core.Models;

public sealed record TargetExecution(
    string RunId,
    TargetSpec Target,
    TargetExecutionState State,
    string? PlannedLocalResultPath,
    string? PlannedRemoteScriptPath,
    DirectExecutionCommand? PlannedCommand = null,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null);
