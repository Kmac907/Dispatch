namespace Dispatch.Core.Models;

public sealed record DispatchRunResult(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string RequestedBy,
    TransportKind Transport,
    PayloadKind PayloadType,
    string PayloadName,
    IReadOnlyList<TargetExecutionResult> Targets,
    string ResultPath)
{
    public long DurationMs => Convert.ToInt64((EndedAt - StartedAt).TotalMilliseconds);

    public int TargetCount => Targets.Count;

    public int SuccessCount => Targets.Count(static target => target.State == TargetExecutionState.Succeeded);

    public int FailedCount => Targets.Count(static target => target.State == TargetExecutionState.Failed);

    public int CancelledCount => Targets.Count(static target => target.State == TargetExecutionState.Cancelled);

    public int TimedOutCount => Targets.Count(static target => target.State == TargetExecutionState.TimedOut);
}
