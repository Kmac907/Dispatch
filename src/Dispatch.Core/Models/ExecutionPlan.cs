namespace Dispatch.Core.Models;

public sealed record ExecutionPlan(
    string RunId,
    DateTimeOffset CreatedAt,
    DispatchJob Job,
    IReadOnlyList<TargetExecution> Targets,
    bool DryRun);
