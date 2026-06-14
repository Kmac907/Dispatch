namespace Dispatch.Core.Models;

public sealed record ExecutionPlan(
    string RunId,
    DateTimeOffset CreatedAt,
    DispatchJob Job,
    IReadOnlyList<TargetExecution> Targets,
    bool DryRun,
    int ThrottleLimit = 0,
    string LocalRunRoot = "",
    string RemoteRunRoot = "",
    string LocalAdminRoot = "",
    string LocalTargetsRoot = "",
    string LocalResultsJsonPath = "",
    string LocalResultsCsvPath = "");
