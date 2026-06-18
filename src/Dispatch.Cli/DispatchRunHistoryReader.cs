using System.Text.Json;
using System.Text.Json.Nodes;
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
        return ReadRunEntry(localRunRoot, selector)?.Result;
    }

    public DispatchRunHistoryEntry? ReadRunEntry(string localRunRoot, string? selector)
    {
        var entries = ListRuns(localRunRoot);
        if (entries.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return entries[0];
        }

        return entries.FirstOrDefault(
            entry => entry.RunId.Equals(selector, StringComparison.OrdinalIgnoreCase));
    }

    public DispatchRunEventTail? ReadRunEvents(string localRunRoot, string? selector, int count)
    {
        var run = ReadRunEntry(localRunRoot, selector);
        if (run is null)
        {
            return null;
        }

        if (!File.Exists(run.EventPath))
        {
            return new DispatchRunEventTail(run.RunId, run.EventPath, []);
        }

        var events = new Queue<DispatchRunEventEntry>();
        foreach (var line in File.ReadLines(run.EventPath).Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            if (events.Count == count)
            {
                events.Dequeue();
            }

            events.Enqueue(ParseEvent(run.RunId, line));
        }

        return new DispatchRunEventTail(run.RunId, run.EventPath, events.ToArray());
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

    private static DispatchRunEventEntry ParseEvent(string runId, string line)
    {
        var parsed = JsonNode.Parse(line) as JsonObject
            ?? throw new JsonException("Dispatch event lines must be JSON objects.");
        var type = parsed["type"]?.GetValue<string>() ?? "unknown";
        var timestamp = TryReadTimestamp(parsed["timestamp"]);
        var target = parsed["target"]?.GetValue<string>();
        var state = parsed["state"]?.GetValue<string>();
        var message = parsed["message"]?.GetValue<string>();

        return new DispatchRunEventEntry(
            runId,
            type,
            timestamp,
            target,
            state,
            message,
            parsed);
    }

    private static DateTimeOffset? TryReadTimestamp(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var text = node.GetValue<string?>();
        return DateTimeOffset.TryParse(text, out var timestamp) ? timestamp : null;
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

internal sealed record DispatchRunEventTail(
    string RunId,
    string EventPath,
    IReadOnlyList<DispatchRunEventEntry> Events);

internal sealed record DispatchRunEventEntry(
    string RunId,
    string Type,
    DateTimeOffset? Timestamp,
    string? Target,
    string? State,
    string? Message,
    JsonObject Event);
