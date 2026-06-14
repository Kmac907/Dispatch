using Dispatch.Core.Configuration;
using Dispatch.Core.Models;
using Dispatch.Core.Validation;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Execution;

internal sealed class DispatchPlanner(
    IOptions<DispatchOptions> options,
    IRunIdGenerator runIdGenerator,
    ISystemClock clock) : IDispatchPlanner
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
        var localRunRoot = Path.Combine(localRoot, runId);
        var localAdminRoot = Path.Combine(localRunRoot, "Admin");
        var remoteRunRoot = CombineWindowsPath(remoteRoot, runId);
        var throttleLimit = request.Throttle ?? options.Value.Throttle;
        var expectedExitCodes = request.ExpectedExitCodes.Count > 0
            ? request.ExpectedExitCodes
            : options.Value.ExpectedExitCodes;

        var requiresEndpointLocalScriptPath = request.Transport == TransportKind.PsExec;
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
            ArtifactPolicy: new ArtifactPolicy(),
            ResultPolicy: new ResultPolicy(localRunRoot));

        var targets = request.Targets
            .Select(target => CreateTargetExecution(runId, target, request.Payload, localRunRoot, remoteRunRoot))
            .ToArray();

        var plan = new ExecutionPlan(
            RunId: runId,
            CreatedAt: clock.UtcNow,
            Job: job,
            Targets: targets,
            DryRun: request.DryRun,
            ThrottleLimit: throttleLimit,
            LocalRunRoot: localRunRoot,
            RemoteRunRoot: remoteRunRoot,
            LocalAdminRoot: localAdminRoot,
            LocalResultsJsonPath: Path.Combine(localAdminRoot, "results.json"),
            LocalResultsCsvPath: Path.Combine(localAdminRoot, "results.csv"));

        return Task.FromResult(plan);
    }

    private static TargetExecution CreateTargetExecution(
        string runId,
        TargetSpec target,
        DispatchPayload payload,
        string localRunRoot,
        string remoteRunRoot)
    {
        var localTargetRoot = Path.Combine(localRunRoot, "Targets", SanitizePathSegment(target.Name));
        var remoteScriptPath = payload is ScriptPayload script
            ? CombineWindowsPath(remoteRunRoot, "script", Path.GetFileName(script.ScriptPath))
            : null;

        var command = payload is ScriptPayload scriptPayload && remoteScriptPath is not null
            ? CreatePowerShellScriptCommand(scriptPayload, remoteScriptPath)
            : null;

        return new TargetExecution(
            RunId: runId,
            Target: target,
            State: TargetExecutionState.Pending,
            PlannedLocalResultPath: Path.Combine(localTargetRoot, "result.json"),
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

    private static DirectExecutionCommand CreatePowerShellScriptCommand(ScriptPayload payload, string remoteScriptPath)
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
        return new DirectExecutionCommand("powershell.exe", arguments);
    }

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

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
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
}
