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
        var artifactFolders = GetArtifactFolders(plan.Job.ArtifactPolicy);

        foreach (var folder in artifactFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteFolder = CombineWindowsPath(plan.RemoteRunRoot, folder);
            var download = await artifactClient.DownloadFolderAsync(
                new PsrpArtifactRequest(
                    target.Target.Name,
                    remoteFolder,
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
                            Message: $"Downloading artifacts from {remoteFolder}",
                            Details: new DispatchExecutionProgressDetails(
                                Operation: "artifact-download",
                                Location: remoteFolder,
                                CompletedBytes: progress.CompletedBytes,
                                TotalBytes: progress.TotalBytes)));
                    },
                    executionContext.PsrpConfigurationName,
                    executionContext.PsrpConnectionKind,
                    executionContext.PsrpAuthentication,
                    executionContext.PsrpCertificateThumbprint),
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

            var localFolder = Path.Combine(target.PlannedLocalTargetRoot, SanitizeRelativePath(folder));
            Directory.CreateDirectory(localFolder);

            try
            {
                copiedArtifacts.AddRange(ExtractArtifacts(download.ZipBytes, localFolder, folder));
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
        var normalizedRelativeRoot = SanitizeRelativePath(relativeRoot).Replace('/', '\\');

        foreach (var entry in archive.Entries.Where(static entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var normalizedEntryPath = entry.FullName.Replace('/', '\\');
            var destinationPath = Path.GetFullPath(Path.Combine(localFolder, normalizedEntryPath));
            if (!destinationPath.StartsWith(localFolderFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Artifact entry '{entry.FullName}' escapes the expected destination root.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            copiedArtifacts.Add(Path.Combine(normalizedRelativeRoot, normalizedEntryPath));
        }

        return copiedArtifacts;
    }

    private static IReadOnlyList<string> GetArtifactFolders(ArtifactPolicy policy) =>
        policy.Paths is { Count: > 0 }
            ? policy.Paths
            : ["logs", "artifacts"];

    private static string CombineWindowsPath(params string[] parts)
    {
        var first = parts[0].TrimEnd('\\');
        var rest = parts
            .Skip(1)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim('\\'));

        return string.Join('\\', new[] { first }.Concat(rest));
    }

    private static string SanitizeRelativePath(string value)
    {
        var invalidPathChars = Path.GetInvalidPathChars();
        var sanitized = string.Concat(value.Select(character => invalidPathChars.Contains(character) ? '_' : character));
        return sanitized.Trim('\\', '/');
    }
}
