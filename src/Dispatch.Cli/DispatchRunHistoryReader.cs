using System.Text.Json;
using Dispatch.Core;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed class DispatchRunHistoryReader
{
    public IReadOnlyList<DispatchRunHistoryEntry> ListRuns(string localRunRoot)
    {
        if (string.IsNullOrWhiteSpace(localRunRoot) || !Directory.Exists(localRunRoot))
        {
            return [];
        }

        return Directory.GetDirectories(localRunRoot)
            .Select(TryReadEntry)
            .OfType<DispatchRunHistoryEntry>()
            .OrderByDescending(static entry => entry.StartedAt)
            .ThenByDescending(static entry => entry.RunId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DispatchRunResult? ReadRun(string localRunRoot, string? selector)
    {
        var entries = ListRuns(localRunRoot);
        if (entries.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return entries[0].Result;
        }

        return entries.FirstOrDefault(
                entry => entry.RunId.Equals(selector, StringComparison.OrdinalIgnoreCase))
            ?.Result;
    }

    private static DispatchRunHistoryEntry? TryReadEntry(string runRoot)
    {
        var resultPath = Path.Combine(runRoot, "Admin", "results.json");
        if (!File.Exists(resultPath))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<DispatchRunResult>(File.ReadAllText(resultPath), DispatchJson.Options);
            if (result is null)
            {
                return null;
            }

            var normalized = string.IsNullOrWhiteSpace(result.ResultPath)
                ? result with { ResultPath = resultPath }
                : result;
            var eventPath = Path.Combine(runRoot, "Admin", "events.ndjson");

            return new DispatchRunHistoryEntry(
                normalized.RunId,
                normalized.StartedAt,
                normalized.EndedAt,
                normalized.Transport,
                normalized.PayloadType,
                normalized.PayloadName,
                normalized.TargetCount,
                normalized.SuccessCount,
                normalized.FailedCount,
                normalized.TimedOutCount,
                normalized.CancelledCount,
                resultPath,
                eventPath,
                normalized);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed record DispatchRunHistoryEntry(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    TransportKind Transport,
    PayloadKind PayloadType,
    string PayloadName,
    int TargetCount,
    int SuccessCount,
    int FailedCount,
    int TimedOutCount,
    int CancelledCount,
    string ResultPath,
    string EventPath,
    DispatchRunResult Result)
{
    public long DurationMs => Result.DurationMs;
}
