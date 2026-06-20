using Dispatch.Core.Credentials;
using System.Text.Json.Serialization;

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
    string LocalResultsCsvPath = "",
    string LocalEventsNdjsonPath = "")
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, DispatchResolvedCredential> RuntimeCredentials { get; init; } =
        new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase);
}
