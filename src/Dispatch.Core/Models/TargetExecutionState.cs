using System.Text.Json.Serialization;

namespace Dispatch.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TargetExecutionState>))]
public enum TargetExecutionState
{
    Pending,
    Resolving,
    Probing,
    PreparingScript,
    Executing,
    CollectingArtifacts,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}
