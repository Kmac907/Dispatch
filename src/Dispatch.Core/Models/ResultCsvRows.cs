namespace Dispatch.Core.Models;

public sealed record RunResultCsvRow(
    string RunId,
    string StartedAt,
    string EndedAt,
    long DurationMs,
    string RequestedBy,
    string Transport,
    string PayloadType,
    string PayloadName,
    int TargetCount,
    int SuccessCount,
    int FailedCount,
    int CancelledCount,
    int TimedOutCount,
    string ResultPath);

public sealed record TargetResultCsvRow(
    string RunId,
    string Target,
    string Transport,
    string PayloadType,
    string PayloadName,
    string State,
    int? ExitCode,
    string ExpectedExitCodes,
    string StartedAt,
    string EndedAt,
    long DurationMs,
    string FailureCategory,
    string? FailureMessage,
    string? StdoutPath,
    string? StderrPath,
    string ResultPath,
    string Artifacts,
    string? SecretHandoffStatus,
    string? CleanupStatus);

public static class ResultCsvMapper
{
    public static RunResultCsvRow ToCsvRow(this DispatchRunResult result) =>
        new(
            result.RunId,
            result.StartedAt.ToString("O"),
            result.EndedAt.ToString("O"),
            result.DurationMs,
            result.RequestedBy,
            result.Transport.ToDispatchString(),
            result.PayloadType.ToString().ToLowerInvariant(),
            result.PayloadName,
            result.TargetCount,
            result.SuccessCount,
            result.FailedCount,
            result.CancelledCount,
            result.TimedOutCount,
            result.ResultPath);

    public static TargetResultCsvRow ToCsvRow(this TargetExecutionResult result) =>
        new(
            result.RunId,
            result.Target,
            result.Transport.ToDispatchString(),
            result.PayloadType.ToString().ToLowerInvariant(),
            result.PayloadName,
            result.State.ToString(),
            result.ExitCode,
            string.Join(';', result.ExpectedExitCodes),
            result.StartedAt.ToString("O"),
            result.EndedAt.ToString("O"),
            result.DurationMs,
            result.FailureCategory.ToString(),
            result.FailureMessage,
            result.StdoutPath,
            result.StderrPath,
            result.ResultPath,
            string.Join(';', result.Artifacts ?? []),
            result.SecretHandoffStatus,
            result.CleanupStatus);
}
