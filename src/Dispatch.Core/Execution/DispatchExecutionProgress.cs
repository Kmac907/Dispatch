using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public sealed record DispatchExecutionProgress(
    string RunId,
    string Target,
    TargetExecutionState State,
    DateTimeOffset Timestamp,
    FailureCategory FailureCategory = FailureCategory.None,
    string? Message = null);
