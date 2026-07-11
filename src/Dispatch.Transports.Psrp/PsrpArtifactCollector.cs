using System.IO.Compression;
using System.Runtime.Versioning;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpArtifactCollector(IPsrpArtifactClient artifactClient) : ITransportArtifactCollector
{
    public TransportKind Kind => TransportKind.Psrp;

    public async Task<ArtifactCollectionResult> CollectAsync(
        ExecutionPlan plan,
        TargetExecution target,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target.PlannedLocalTargetRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No local target root was planned.");
        }

        if (string.IsNullOrWhiteSpace(plan.RemoteRunRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No remote run path was planned.");
        }

        var executionContext = plan.Job.ExecutionContext;
        var copiedArtifacts = new List<string>();
        var artifactFolders = ArtifactCollectionPathResolver.ResolveAll(plan.Job.ArtifactPolicy, plan.RemoteRunRoot);

        foreach (var artifactPath in artifactFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var download = await artifactClient.DownloadFolderAsync(
                new PsrpArtifactRequest(
                    target.Target.Name,
                    artifactPath.RemoteFolder,
                    plan.Job.TimeoutPolicy.ExecutionTimeout,
                    progress =>
                    {
                        if (progressReporter is null)
                        {
                            return;
                        }

                        progressReporter(new DispatchExecutionProgress(
                            plan.RunId,
                            target.Target.Name,
                            TargetExecutionState.CollectingArtifacts,
                            DateTimeOffset.UtcNow,
                            Message: $"Downloading artifacts from {artifactPath.RemoteFolder}",
                            Details: new DispatchExecutionProgressDetails(
                                Operation: "artifact-download",
                                Location: artifactPath.RemoteFolder,
                                CompletedBytes: progress.CompletedBytes,
                                TotalBytes: progress.TotalBytes)));
                    },
                    executionContext.PsrpConfigurationName,
                    executionContext.PsrpConnectionKind,
                    executionContext.PsrpAuthentication,
                    executionContext.PsrpCertificateThumbprint,
                    ResolveTargetCredential(plan, target)),
                cancellationToken).ConfigureAwait(false);

            if (!download.Succeeded)
            {
                return new ArtifactCollectionResult(
                    "failed",
                    copiedArtifacts,
                    download.FailureMessage ?? $"Artifact collection failed for target '{target.Target.Name}'.");
            }

            if (download.IsMissing || download.ZipBytes.Length == 0)
            {
                continue;
            }

            var localFolder = Path.Combine(target.PlannedLocalTargetRoot, artifactPath.LocalRelativeRoot);
            Directory.CreateDirectory(localFolder);

            try
            {
                copiedArtifacts.AddRange(ExtractArtifacts(download.ZipBytes, localFolder, artifactPath.LocalRelativeRoot));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                return new ArtifactCollectionResult(
                    "failed",
                    copiedArtifacts,
                    $"Artifact collection failed for target '{target.Target.Name}': {exception.Message}");
            }
        }

        return new ArtifactCollectionResult(
            copiedArtifacts.Count > 0 ? "collected" : "not-found",
            copiedArtifacts);
    }

    private static IReadOnlyList<string> ExtractArtifacts(byte[] zipBytes, string localFolder, string relativeRoot)
    {
        using var archiveStream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var copiedArtifacts = new List<string>();
        var localFolderFullPath = Path.GetFullPath(localFolder);
        var localFolderBoundary = localFolderFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? localFolderFullPath
            : localFolderFullPath + Path.DirectorySeparatorChar;
        var normalizedRelativeRoot = SanitizeRelativePath(relativeRoot).Replace('/', '\\');

        foreach (var entry in archive.Entries.Where(static entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var normalizedEntryPath = entry.FullName.Replace('/', '\\');
            var destinationPath = Path.GetFullPath(Path.Combine(localFolder, normalizedEntryPath));
            if (!destinationPath.StartsWith(localFolderBoundary, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Artifact entry '{entry.FullName}' escapes the expected destination root.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            copiedArtifacts.Add(Path.Combine(normalizedRelativeRoot, normalizedEntryPath));
        }

        return copiedArtifacts;
    }

    private static Dispatch.Core.Credentials.DispatchResolvedCredential? ResolveTargetCredential(
        ExecutionPlan plan,
        TargetExecution target)
    {
        var reference = target.Target.CredentialReference;
        return string.IsNullOrWhiteSpace(reference)
            ? null
            : plan.RuntimeCredentials.TryGetValue(reference.Trim(), out var credential)
                ? credential
                : null;
    }

    private static string SanitizeRelativePath(string value)
    {
        return ArtifactCollectionPathResolver.SanitizeRelativePath(value);
    }
}
