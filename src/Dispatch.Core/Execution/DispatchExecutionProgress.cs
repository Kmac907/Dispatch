using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public sealed record DispatchExecutionProgress(
    string RunId,
    string Target,
    TargetExecutionState State,
    DateTimeOffset Timestamp,
    FailureCategory FailureCategory = FailureCategory.None,
    string? Message = null,
    DispatchExecutionProgressDetails? Details = null);

public sealed record DispatchExecutionProgressDetails(
    string? Operation = null,
    string? Location = null,
    int? CompletedUnits = null,
    int? TotalUnits = null,
    string? UnitLabel = null,
    long? CompletedBytes = null,
    long? TotalBytes = null);
