using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

internal sealed class DispatchArtifactCollector(
    IEndpointFileSystem endpointFileSystem,
    IEnumerable<ITransportArtifactCollector> transportArtifactCollectors) : IDispatchArtifactCollector
{
    public async Task<ArtifactCollectionResult> CollectAsync(
        ExecutionPlan plan,
        TargetExecution target,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.Job.Transport != TransportKind.PsExec)
        {
            var transportCollector = transportArtifactCollectors.SingleOrDefault(collector => collector.Kind == plan.Job.Transport);
            if (transportCollector is not null)
            {
                return await transportCollector.CollectAsync(plan, target, cancellationToken, progressReporter).ConfigureAwait(false);
            }

            return new ArtifactCollectionResult(
                "skipped",
                [],
                $"Artifact collection is not implemented for transport '{plan.Job.Transport.ToDispatchString()}' in this slice.");
        }

        if (string.IsNullOrWhiteSpace(target.PlannedLocalTargetRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No local target root was planned.");
        }

        if (string.IsNullOrWhiteSpace(plan.RemoteRunRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No remote run path was planned.");
        }

        var copiedArtifacts = new List<string>();

        try
        {
            foreach (var artifactPath in ArtifactCollectionPathResolver.ResolveAll(plan.Job.ArtifactPolicy, plan.RemoteRunRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var adminShareFolder = AdminSharePath.FromRemoteWindowsPath(target.Target.Name, artifactPath.RemoteFolder);
                if (!adminShareFolder.IsValid)
                {
                    return new ArtifactCollectionResult("failed", copiedArtifacts, adminShareFolder.Error!.Message);
                }

                if (!await endpointFileSystem.DirectoryExistsAsync(adminShareFolder.Path!, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var localFolder = Path.Combine(target.PlannedLocalTargetRoot, artifactPath.LocalRelativeRoot);
                var copiedFiles = await endpointFileSystem
                    .CopyDirectoryAsync(adminShareFolder.Path!, localFolder, overwrite: true, cancellationToken)
                    .ConfigureAwait(false);

                copiedArtifacts.AddRange(copiedFiles.Select(file => Path.GetRelativePath(target.PlannedLocalTargetRoot, file)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ArtifactCollectionResult("failed", copiedArtifacts, $"Artifact copy-back failed for target '{target.Target.Name}': {exception.Message}");
        }

        return new ArtifactCollectionResult(
            copiedArtifacts.Count > 0 ? "collected" : "not-found",
            copiedArtifacts);
    }

}
