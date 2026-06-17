using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

internal sealed class ScriptPreparationService(IEndpointFileSystem endpointFileSystem) : IScriptPreparationService
{
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
                    null));
                targetResults.Add(new TargetScriptPreparationResult(
                    Target: target.Target,
                    RemoteScriptPath: target.PlannedRemoteScriptPath,
                    AdminShareScriptPath: null,
                    Succeeded: true));
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
