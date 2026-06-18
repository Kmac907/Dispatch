using Dispatch.Core;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed class DispatchRunRetryPlanner
{
    public DispatchRunRetryPlan Create(DispatchRunHistoryEntry run)
    {
        var retryTargets = run.Result.Targets
            .Where(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled)
            .Select(static target => new DispatchRunRetryTarget(
                target.Target,
                target.State,
                target.FailureCategory,
                target.FailureMessage,
                target.ExitCode))
            .ToArray();

        var suggestedCommand = TryBuildSuggestedCommand(run.Result, retryTargets, out var reason);
        return new DispatchRunRetryPlan(
            run.RunId,
            run.Transport,
            run.PayloadType,
            run.PayloadName,
            retryTargets,
            suggestedCommand is not null,
            suggestedCommand,
            reason);
    }

    private static string? TryBuildSuggestedCommand(
        DispatchRunResult result,
        IReadOnlyList<DispatchRunRetryTarget> retryTargets,
        out string reason)
    {
        if (retryTargets.Count == 0)
        {
            reason = "The selected run has no failed, timed-out, or cancelled targets.";
            return null;
        }

        if (result.PayloadType != PayloadKind.Command)
        {
            reason = "Script retry cannot be reconstructed from results.json because the original script path and arguments are not persisted in the final summary.";
            return null;
        }

        var shell = result.Targets
            .Select(static target => TryReadMetadata(target, "commandShell"))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (!string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Command retry hints currently require a cmd payload, but this run recorded shell '{shell ?? "unknown"}'.";
            return null;
        }

        var expectedExitCodes = result.Targets
            .SelectMany(static target => target.ExpectedExitCodes)
            .Distinct()
            .Order()
            .ToArray();
        var expectedExitCodeArg = expectedExitCodes.Length == 0
            ? "0"
            : string.Join(',', expectedExitCodes);
        var targets = string.Join(',', retryTargets.Select(static target => target.Target));
        reason = "Review the retry plan before running the suggested command; Dispatch does not automatically re-execute endpoints from logs in this v1 slice.";
        return string.Join(
            ' ',
            "dispatch",
            "run",
            "cmd",
            Quote(result.PayloadName),
            "--target",
            Quote(targets),
            "--transport",
            result.Transport.ToDispatchString(),
            "--expected-exit-code",
            Quote(expectedExitCodeArg));
    }

    private static string? TryReadMetadata(TargetExecutionResult target, string key)
    {
        if (target.TransportMetadata is null)
        {
            return null;
        }

        return target.TransportMetadata.TryGetValue(key, out var value) ? value : null;
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.All(static character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}

internal sealed record DispatchRunRetryPlan(
    string RunId,
    TransportKind Transport,
    PayloadKind PayloadType,
    string PayloadName,
    IReadOnlyList<DispatchRunRetryTarget> Targets,
    bool ReexecutionSupported,
    string? SuggestedCommand,
    string Message)
{
    public int RetryTargetCount => Targets.Count;
}

internal sealed record DispatchRunRetryTarget(
    string Target,
    TargetExecutionState State,
    FailureCategory FailureCategory,
    string? FailureMessage,
    int? ExitCode);
