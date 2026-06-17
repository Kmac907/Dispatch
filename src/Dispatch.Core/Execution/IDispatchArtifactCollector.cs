using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface IDispatchArtifactCollector
{
    Task<ArtifactCollectionResult> CollectAsync(
        ExecutionPlan plan,
        TargetExecution target,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter = null);
}

public interface ITransportArtifactCollector
{
    TransportKind Kind { get; }

    Task<ArtifactCollectionResult> CollectAsync(
        ExecutionPlan plan,
        TargetExecution target,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter = null);
}

public sealed record ArtifactCollectionResult(
    string Status,
    IReadOnlyList<string> Artifacts,
    string? FailureMessage = null);
