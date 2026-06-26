using Dispatch.Core.Configuration;
using Dispatch.Core.Models;
using Dispatch.Core.Validation;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Execution;

internal sealed class DispatchPlanner(
    IOptions<DispatchOptions> options,
    IRunIdGenerator runIdGenerator,
    ISystemClock clock,
    ILocalRunLayoutService localRunLayoutService) : IDispatchPlanner
{
    public Task<ExecutionPlan> CreatePlanAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            throw new DispatchPlanningException(errors);
        }

        var runId = runIdGenerator.CreateRunId();
        var localRoot = request.LocalRunRoot ?? options.Value.LocalRunRoot;
        var remoteRoot = request.RemoteRunRoot ?? options.Value.RemoteRunRoot;
        var localLayoutResult = localRunLayoutService.Prepare(
            localRoot,
            runId,
            request.Targets,
            createDirectories: !request.DryRun);

        if (!localLayoutResult.IsValid)
        {
            throw new DispatchPlanningException(localLayoutResult.Errors);
        }

        var localLayout = localLayoutResult.Layout!;
        var remoteRunRoot = CombineWindowsPath(remoteRoot, runId);
        var throttleLimit = request.Throttle ?? options.Value.Throttle;
        var expectedExitCodes = request.ExpectedExitCodes.Count > 0
            ? request.ExpectedExitCodes
            : options.Value.ExpectedExitCodes;

        var requiresEndpointLocalScriptPath =
            request.Payload is ScriptPayload
            && (request.Transport == TransportKind.PsExec
                || request.Transport == TransportKind.WinRm);
        var scriptSecretPlans = CreateScriptSecretPlans(request.ScriptSecrets, remoteRunRoot);
        var job = new DispatchJob(
            RunId: runId,
            Targets: request.Targets,
            Payload: request.Payload,
            Transport: request.Transport,
            ExecutionContext: request.ExecutionContext,
            ScriptTransferPolicy: new ScriptTransferPolicy(remoteRunRoot, requiresEndpointLocalScriptPath),
            TimeoutPolicy: new TimeoutPolicy(),
            RetryPolicy: new RetryPolicy(),
            ExpectedExitCodes: expectedExitCodes,
            ArtifactPolicy: new ArtifactPolicy(request.ArtifactPaths),
            ResultPolicy: new ResultPolicy(localLayout.LocalRunRoot),
            ScriptSecrets: scriptSecretPlans);

        var targets = localLayout.Targets
            .Select(target => CreateTargetExecution(runId, target, request.Payload, remoteRunRoot, scriptSecretPlans))
            .ToArray();

        var plan = new ExecutionPlan(
            RunId: runId,
            CreatedAt: clock.UtcNow,
            Job: job,
            Targets: targets,
            DryRun: request.DryRun,
            ThrottleLimit: throttleLimit,
            LocalRunRoot: localLayout.LocalRunRoot,
            RemoteRunRoot: remoteRunRoot,
            LocalAdminRoot: localLayout.LocalAdminRoot,
            LocalTargetsRoot: localLayout.LocalTargetsRoot,
            LocalResultsJsonPath: localLayout.LocalResultsJsonPath,
            LocalResultsCsvPath: localLayout.LocalResultsCsvPath,
            LocalEventsNdjsonPath: localLayout.LocalEventsNdjsonPath);

        return Task.FromResult(plan);
    }

    private static TargetExecution CreateTargetExecution(
        string runId,
        TargetLocalLayout targetLayout,
        DispatchPayload payload,
        string remoteRunRoot,
        IReadOnlyList<ScriptSecretHandoffPlan> scriptSecrets)
    {
        var remoteScriptPath = payload is ScriptPayload script
            ? CombineWindowsPath(remoteRunRoot, "script", Path.GetFileName(script.ScriptPath))
            : null;

        var command = payload switch
        {
            ScriptPayload scriptPayload when remoteScriptPath is not null => CreatePowerShellScriptCommand(scriptPayload, remoteScriptPath, scriptSecrets),
            CommandPayload commandPayload => CreateCommandPayloadCommand(commandPayload),
            _ => null
        };

        return new TargetExecution(
            RunId: runId,
            Target: targetLayout.Target,
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: targetLayout.LocalTargetRoot,
            PlannedLocalResultPath: targetLayout.LocalResultPath,
            PlannedRemoteScriptPath: remoteScriptPath,
            PlannedCommand: command);
    }

    private IReadOnlyList<DispatchValidationError> Validate(DispatchRequest request)
    {
        var errors = new List<DispatchValidationError>();
        var requestValidation = DispatchRequestValidator.Validate(request);
        errors.AddRange(requestValidation.Errors);

        if (request.Throttle is <= 0)
        {
            errors.Add(new("InvalidThrottle", "Throttle must be greater than zero."));
        }

        var localRunRoot = request.LocalRunRoot ?? options.Value.LocalRunRoot;
        if (string.IsNullOrWhiteSpace(localRunRoot))
        {
            errors.Add(new("LocalRunRootRequired", "Local run root is required."));
        }
        else if (!IsValidLocalRoot(localRunRoot))
        {
            errors.Add(new("InvalidLocalRunRoot", $"Local run root '{localRunRoot}' is not a valid local path."));
        }

        var remoteRunRoot = request.RemoteRunRoot ?? options.Value.RemoteRunRoot;
        if (string.IsNullOrWhiteSpace(remoteRunRoot))
        {
            errors.Add(new("RemoteRunRootRequired", "Remote run root is required."));
        }
        else if (!IsRootedWindowsPath(remoteRunRoot))
        {
            errors.Add(new("InvalidRemoteRunRoot", $"Remote run root '{remoteRunRoot}' must be a rooted Windows path."));
        }

        switch (request.Payload)
        {
            case ScriptPayload script:
                ValidateScriptPayload(script, errors);
                break;
            case CommandPayload command:
                if (string.IsNullOrWhiteSpace(command.CommandLine))
                {
                    errors.Add(new("CommandRequired", "Command line is required."));
                }

                if (string.IsNullOrWhiteSpace(command.Shell))
                {
                    errors.Add(new("CommandShellRequired", "Command shell is required."));
                }
                else if (!IsSupportedCommandShell(command.Shell))
                {
                    errors.Add(new("UnsupportedCommandShell", $"Command shell '{command.Shell}' is not supported."));
                }

                break;
        }

        return errors;
    }

    private static void ValidateScriptPayload(ScriptPayload script, ICollection<DispatchValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(script.ScriptPath))
        {
            errors.Add(new("ScriptPathRequired", "Script path is required."));
            return;
        }

        if (!File.Exists(script.ScriptPath))
        {
            errors.Add(new("ScriptNotFound", $"Script path '{script.ScriptPath}' does not exist."));
        }
    }

    private static DirectExecutionCommand CreatePowerShellScriptCommand(
        ScriptPayload payload,
        string remoteScriptPath,
        IReadOnlyList<ScriptSecretHandoffPlan> scriptSecrets)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            remoteScriptPath
        };

        arguments.AddRange(payload.ScriptArguments);
        foreach (var secret in scriptSecrets)
        {
            arguments.Add(secret.ScriptParameterName);
            arguments.Add(secret.RedactedValue);
        }

        return new DirectExecutionCommand("powershell.exe", arguments);
    }

    private static IReadOnlyList<ScriptSecretHandoffPlan> CreateScriptSecretPlans(
        IReadOnlyList<ScriptSecretReference> references,
        string remoteRunRoot) =>
        references
            .Select(reference => new ScriptSecretHandoffPlan(
                reference.Name,
                reference.ReferenceName,
                $"-{reference.Name}"))
            .ToArray();

    private static DirectExecutionCommand CreateCommandPayloadCommand(CommandPayload payload)
    {
        var normalizedShell = payload.Shell.Trim().ToLowerInvariant();
        return normalizedShell switch
        {
            "cmd" or "exe" or "direct" => new DirectExecutionCommand(
                "cmd.exe",
                ["/c", WrapCmdCommand(payload.CommandLine, payload.WorkingDirectory)]),
            "powershell" or "powershell.exe" => CreatePowerShellCommand("powershell.exe", payload.CommandLine, payload.WorkingDirectory),
            "pwsh" or "pwsh.exe" => CreatePowerShellCommand("pwsh.exe", payload.CommandLine, payload.WorkingDirectory),
            _ => throw new InvalidOperationException($"Unsupported command shell '{payload.Shell}'.")
        };
    }

    private static DirectExecutionCommand CreatePowerShellCommand(
        string executable,
        string commandLine,
        string? workingDirectory)
    {
        var effectiveCommand = string.IsNullOrWhiteSpace(workingDirectory)
            ? commandLine
            : $"Set-Location -LiteralPath '{EscapePowerShellSingleQuotedString(workingDirectory)}'; {commandLine}";

        return new DirectExecutionCommand(
            executable,
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", effectiveCommand]);
    }

    private static string WrapCmdCommand(string commandLine, string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? commandLine
            : $"cd /d \"{workingDirectory.Replace("\"", "\"\"", StringComparison.Ordinal)}\" && {commandLine}";

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string CombineWindowsPath(params string[] parts)
    {
        var nonEmptyParts = parts
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim('\\'))
            .ToArray();

        if (nonEmptyParts.Length == 0)
        {
            return string.Empty;
        }

        var first = parts[0].TrimEnd('\\');
        return string.Join('\\', new[] { first }.Concat(nonEmptyParts.Skip(1)));
    }

    private static bool IsValidLocalRoot(string path)
    {
        try
        {
            _ = Path.GetFullPath(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsRootedWindowsPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path.Length > 2;
        }

        return path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    private static bool IsSupportedCommandShell(string shell)
    {
        var normalized = shell.Trim();
        return normalized.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("exe", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("direct", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }
}
