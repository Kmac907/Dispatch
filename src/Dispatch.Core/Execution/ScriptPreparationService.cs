using Dispatch.Core.Models;
using System.Security.Cryptography;

namespace Dispatch.Core.Execution;

internal sealed class ScriptPreparationService(IEndpointFileSystem endpointFileSystem) : IScriptPreparationService
{
    private const int WinRmChunkSizeBytes = 8192;

    public async Task<ScriptPreparationResult> PrepareAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.Job.Payload is not ScriptPayload script)
        {
            var unsupportedTargets = plan.Targets
                .Select(target => Failed(
                    target.Target,
                    target.PlannedRemoteScriptPath ?? string.Empty,
                    null,
                    FailureCategory.PayloadPreparationFailed,
                    "Script preparation requires a script payload."))
                .ToArray();

            return new ScriptPreparationResult(null, unsupportedTargets);
        }

        var targetManifests = new List<TargetScriptManifest>();
        var targetResults = new List<TargetScriptPreparationResult>();
        ScriptTransferPlan? winRmTransferPlan = null;

        if (UsesChunkedWinRmScriptTransfer(plan.Job.Transport))
        {
            try
            {
                winRmTransferPlan = await CreateWinRmTransferPlanAsync(script.ScriptPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var failedTargets = plan.Targets
                    .Select(target => Failed(
                        target.Target,
                        target.PlannedRemoteScriptPath ?? string.Empty,
                        null,
                        FailureCategory.PayloadPreparationFailed,
                        $"Failed to prepare WinRM script payload for target '{target.Target.Name}': {exception.Message}"))
                    .ToArray();

                return new ScriptPreparationResult(null, failedTargets);
            }
        }

        foreach (var target in plan.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(target.PlannedRemoteScriptPath))
            {
                targetResults.Add(Failed(
                    target.Target,
                    string.Empty,
                    null,
                    FailureCategory.PayloadPreparationFailed,
                    $"No planned remote script path exists for target '{target.Target.Name}'."));
                continue;
            }

            if (!UsesAdminShareScriptTransfer(plan.Job.Transport))
            {
                targetManifests.Add(new TargetScriptManifest(
                    target.Target,
                    target.PlannedRemoteScriptPath,
                    null,
                    winRmTransferPlan));
                targetResults.Add(new TargetScriptPreparationResult(
                    Target: target.Target,
                    RemoteScriptPath: target.PlannedRemoteScriptPath,
                    AdminShareScriptPath: null,
                    Succeeded: true,
                    TransferPlan: winRmTransferPlan));
                continue;
            }

            var adminSharePath = AdminSharePath.FromRemoteWindowsPath(target.Target.Name, target.PlannedRemoteScriptPath);
            if (!adminSharePath.IsValid)
            {
                targetResults.Add(Failed(
                    target.Target,
                    target.PlannedRemoteScriptPath,
                    null,
                    FailureCategory.PayloadPreparationFailed,
                    adminSharePath.Error!.Message));
                continue;
            }

            targetManifests.Add(new TargetScriptManifest(
                target.Target,
                target.PlannedRemoteScriptPath,
                adminSharePath.Path));

            if (plan.DryRun)
            {
                targetResults.Add(new TargetScriptPreparationResult(
                    Target: target.Target,
                    RemoteScriptPath: target.PlannedRemoteScriptPath,
                    AdminShareScriptPath: adminSharePath.Path,
                    Succeeded: true));
                continue;
            }

            try
            {
                var destinationDirectory = Path.GetDirectoryName(adminSharePath.Path!);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    targetResults.Add(Failed(
                        target.Target,
                        target.PlannedRemoteScriptPath,
                        adminSharePath.Path,
                        FailureCategory.PayloadPreparationFailed,
                        $"Could not determine destination directory for '{adminSharePath.Path}'."));
                    continue;
                }

                await endpointFileSystem.CreateDirectoryAsync(destinationDirectory, cancellationToken).ConfigureAwait(false);
                await endpointFileSystem.CopyFileAsync(script.ScriptPath, adminSharePath.Path!, overwrite: true, cancellationToken).ConfigureAwait(false);

                targetResults.Add(new TargetScriptPreparationResult(
                    Target: target.Target,
                    RemoteScriptPath: target.PlannedRemoteScriptPath,
                    AdminShareScriptPath: adminSharePath.Path,
                    Succeeded: true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                targetResults.Add(Failed(
                    target.Target,
                    target.PlannedRemoteScriptPath,
                    adminSharePath.Path,
                    FailureCategory.ScriptTransferFailed,
                    $"Failed to copy script to target '{target.Target.Name}': {exception.Message}"));
            }
        }

        var manifest = new ScriptExecutionManifest(
            SourceScriptPath: script.ScriptPath,
            ScriptArguments: script.ScriptArguments,
            RemoteScriptDirectory: Path.GetDirectoryName(plan.Targets.FirstOrDefault()?.PlannedRemoteScriptPath ?? string.Empty) ?? string.Empty,
            Targets: targetManifests);

        return new ScriptPreparationResult(manifest, targetResults);
    }

    private static bool UsesAdminShareScriptTransfer(TransportKind transport) => transport == TransportKind.PsExec;

    private static bool UsesChunkedWinRmScriptTransfer(TransportKind transport) => transport == TransportKind.WinRm;

    private static async Task<ScriptTransferPlan> CreateWinRmTransferPlanAsync(
        string scriptPath,
        CancellationToken cancellationToken)
    {
        var scriptBytes = await File.ReadAllBytesAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        var chunks = new List<ScriptTransferChunk>((scriptBytes.Length + WinRmChunkSizeBytes - 1) / WinRmChunkSizeBytes);

        for (var offset = 0; offset < scriptBytes.Length; offset += WinRmChunkSizeBytes)
        {
            var chunkLength = Math.Min(WinRmChunkSizeBytes, scriptBytes.Length - offset);
            var chunkBytes = scriptBytes.AsSpan(offset, chunkLength).ToArray();
            chunks.Add(new ScriptTransferChunk(
                Index: chunks.Count,
                Offset: offset,
                ByteLength: chunkLength,
                Sha256: ComputeSha256(chunkBytes),
                Base64Data: Convert.ToBase64String(chunkBytes)));
        }

        return new ScriptTransferPlan(
            Mode: ScriptTransferMode.WinRmChunkedBase64,
            TotalBytes: scriptBytes.Length,
            ContentSha256: ComputeSha256(scriptBytes),
            ChunkSizeBytes: WinRmChunkSizeBytes,
            Chunks: chunks);
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static TargetScriptPreparationResult Failed(
        TargetSpec target,
        string remoteScriptPath,
        string? adminShareScriptPath,
        FailureCategory failureCategory,
        string failureMessage) =>
        new(
            Target: target,
            RemoteScriptPath: remoteScriptPath,
            AdminShareScriptPath: adminShareScriptPath,
            Succeeded: false,
            FailureCategory: failureCategory,
            FailureMessage: failureMessage);
}
