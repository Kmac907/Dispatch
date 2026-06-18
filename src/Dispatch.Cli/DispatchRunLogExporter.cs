using System.Text;
using Dispatch.Core;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed class DispatchRunLogExporter
{
    public DispatchRunLogExportResult Export(DispatchRunHistoryEntry run, string destinationRoot)
    {
        var exportRoot = Path.Combine(Path.GetFullPath(destinationRoot), run.RunId);
        Directory.CreateDirectory(exportRoot);

        var resultsJsonPath = Path.Combine(exportRoot, "results.json");
        File.Copy(run.ResultPath, resultsJsonPath, overwrite: true);

        string? eventsNdjsonPath = null;
        if (File.Exists(run.EventPath))
        {
            eventsNdjsonPath = Path.Combine(exportRoot, "events.ndjson");
            File.Copy(run.EventPath, eventsNdjsonPath, overwrite: true);
        }

        var resultsCsvPath = Path.Combine(exportRoot, "results.csv");
        File.WriteAllText(resultsCsvPath, CreateCsv(run.Result), Encoding.UTF8);

        return new DispatchRunLogExportResult(
            run.RunId,
            exportRoot,
            resultsJsonPath,
            eventsNdjsonPath,
            resultsCsvPath);
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

internal sealed record DispatchRunLogExportResult(
    string RunId,
    string ExportRoot,
    string ResultsJsonPath,
    string? EventsNdjsonPath,
    string ResultsCsvPath);
