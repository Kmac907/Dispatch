using System.Text;
using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

internal sealed class DispatchResultWriter : IDispatchResultWriter
{
    public async Task WriteAsync(ExecutionPlan plan, DispatchRunResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(plan.LocalAdminRoot);
        Directory.CreateDirectory(plan.LocalTargetsRoot);

        foreach (var target in result.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPlan = plan.Targets.SingleOrDefault(candidate =>
                candidate.Target.Name.Equals(target.Target, StringComparison.OrdinalIgnoreCase));
            var targetRoot = targetPlan?.PlannedLocalTargetRoot;
            if (!string.IsNullOrWhiteSpace(targetRoot))
            {
                Directory.CreateDirectory(targetRoot);
            }

            if (!string.IsNullOrWhiteSpace(target.ResultPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target.ResultPath)!);
                await File.WriteAllTextAsync(target.ResultPath, DispatchJson.Serialize(target), cancellationToken).ConfigureAwait(false);
            }
        }

        await File.WriteAllTextAsync(plan.LocalResultsJsonPath, DispatchJson.Serialize(result), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(plan.LocalResultsCsvPath, CreateCsv(result), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(plan.LocalAdminRoot, "dispatch.log"), CreateLog(result), cancellationToken).ConfigureAwait(false);
    }

    private static string CreateCsv(DispatchRunResult result)
    {
        var builder = new StringBuilder();
        AppendCsvLine(
            builder,
            "RunId",
            "RunStartedAt",
            "RunEndedAt",
            "RunDurationMs",
            "RequestedBy",
            "Transport",
            "PayloadType",
            "PayloadName",
            "TargetCount",
            "SuccessCount",
            "FailedCount",
            "CancelledCount",
            "TimedOutCount",
            "Target",
            "State",
            "ExitCode",
            "ExpectedExitCodes",
            "TargetStartedAt",
            "TargetEndedAt",
            "TargetDurationMs",
            "FailureCategory",
            "FailureMessage",
            "StdoutPath",
            "StderrPath",
            "ResultPath",
            "Artifacts",
            "ArtifactCollectionStatus",
            "ArtifactCollectionFailureMessage",
            "SecretHandoffStatus",
            "CleanupStatus");

        foreach (var target in result.Targets)
        {
            AppendCsvLine(
                builder,
                result.RunId,
                result.StartedAt.ToString("O"),
                result.EndedAt.ToString("O"),
                result.DurationMs.ToString(),
                result.RequestedBy,
                result.Transport.ToDispatchString(),
                result.PayloadType.ToString().ToLowerInvariant(),
                result.PayloadName,
                result.TargetCount.ToString(),
                result.SuccessCount.ToString(),
                result.FailedCount.ToString(),
                result.CancelledCount.ToString(),
                result.TimedOutCount.ToString(),
                target.Target,
                target.State.ToString(),
                target.ExitCode?.ToString() ?? string.Empty,
                string.Join(';', target.ExpectedExitCodes),
                target.StartedAt.ToString("O"),
                target.EndedAt.ToString("O"),
                target.DurationMs.ToString(),
                target.FailureCategory.ToString(),
                target.FailureMessage ?? string.Empty,
                target.StdoutPath ?? string.Empty,
                target.StderrPath ?? string.Empty,
                target.ResultPath,
                string.Join(';', target.Artifacts ?? []),
                target.ArtifactCollectionStatus ?? string.Empty,
                target.ArtifactCollectionFailureMessage ?? string.Empty,
                target.SecretHandoffStatus ?? string.Empty,
                target.CleanupStatus ?? string.Empty);
        }

        return builder.ToString();
    }

    private static string CreateLog(DispatchRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"RunId: {result.RunId}");
        builder.AppendLine($"StartedAt: {result.StartedAt:O}");
        builder.AppendLine($"EndedAt: {result.EndedAt:O}");
        builder.AppendLine($"Transport: {result.Transport.ToDispatchString()}");
        builder.AppendLine($"Payload: {result.PayloadType.ToString().ToLowerInvariant()} {result.PayloadName}");
        builder.AppendLine($"Targets: {result.TargetCount}; Succeeded: {result.SuccessCount}; Failed: {result.FailedCount}; TimedOut: {result.TimedOutCount}; Cancelled: {result.CancelledCount}");

        foreach (var target in result.Targets)
        {
            var failure = target.FailureCategory == FailureCategory.None
                ? string.Empty
                : $"; Failure: {target.FailureCategory} {target.FailureMessage}";
            var artifactStatus = string.IsNullOrWhiteSpace(target.ArtifactCollectionStatus)
                ? string.Empty
                : $"; Artifacts: {target.ArtifactCollectionStatus}";
            builder.AppendLine($"Target: {target.Target}; State: {target.State}; ExitCode: {target.ExitCode?.ToString() ?? ""}{failure}{artifactStatus}");
        }

        return builder.ToString();
    }

    private static void AppendCsvLine(StringBuilder builder, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(EscapeCsv(values[index]));
        }

        builder.AppendLine();
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
