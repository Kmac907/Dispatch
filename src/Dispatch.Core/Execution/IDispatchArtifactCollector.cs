using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IDispatchArtifactCollector
{
    Task<ArtifactCollectionResult> CollectAsync(ExecutionPlan plan, TargetExecution target, CancellationToken cancellationToken);
}

public sealed record ArtifactCollectionResult(
    string Status,
    IReadOnlyList<string> Artifacts,
    string? FailureMessage = null);
