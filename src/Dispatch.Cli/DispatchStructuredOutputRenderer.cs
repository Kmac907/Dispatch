using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Core;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal static class DispatchStructuredOutputRenderer
{
    public static void RenderPlan(TextWriter writer, ExecutionPlan plan, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(plan));
                break;
            case DispatchOutputMode.Ndjson:
                var planStream = new DispatchNdjsonStreamWriter(writer, verbose: false, trace: false);
                planStream.WritePlanningStarted();
                planStream.WritePlan(plan);
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, plan);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderDryRunPlan(writer, plan);
                break;
        }
    }

    public static void RenderRunResult(TextWriter writer, DispatchRunResult result, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                var resultStream = new DispatchNdjsonStreamWriter(writer, verbose: false, trace: false);
                resultStream.WriteResult(result);
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderRunResult(writer, result);
                break;
        }
    }

    public static void RenderRunHistory(TextWriter writer, string localRunRoot, IReadOnlyList<DispatchRunHistoryEntry> runs, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(runs));
                break;
            case DispatchOutputMode.Ndjson:
                foreach (var run in runs)
                {
                    writer.WriteLine(JsonSerializer.Serialize(
                        run,
                        new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, runs);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderRunHistory(writer, localRunRoot, runs);
                break;
        }
    }

    public static void RenderRunEventTail(TextWriter writer, DispatchRunEventTail tail, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(tail));
                break;
            case DispatchOutputMode.Ndjson:
                foreach (var entry in tail.Events)
                {
                    writer.WriteLine(entry.Event.ToJsonString(new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, tail);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderRunEventTail(writer, tail);
                break;
        }
    }

    private static void WriteYaml<T>(TextWriter writer, T value)
    {
        var node = JsonSerializer.SerializeToNode(value, DispatchJson.Options);
        if (node is not null)
        {
            WriteYamlNode(writer, node, 0);
        }
    }

    private static void WriteYamlNode(TextWriter writer, JsonNode node, int indent)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    WriteIndent(writer, indent);
                    writer.Write(property.Key);
                    writer.Write(':');
                    if (property.Value is JsonObject or JsonArray)
                    {
                        writer.WriteLine();
                        WriteYamlNode(writer, property.Value, indent + 2);
                    }
                    else
                    {
                        writer.Write(' ');
                        WriteScalar(writer, property.Value);
                        writer.WriteLine();
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    WriteIndent(writer, indent);
                    writer.Write("- ");
                    if (item is JsonObject or JsonArray)
                    {
                        writer.WriteLine();
                        WriteYamlNode(writer, item, indent + 2);
                    }
                    else
                    {
                        WriteScalar(writer, item);
                        writer.WriteLine();
                    }
                }

                break;
            default:
                WriteIndent(writer, indent);
                WriteScalar(writer, node);
                writer.WriteLine();
                break;
        }
    }

    private static void WriteScalar(TextWriter writer, JsonNode? node)
    {
        if (node is null)
        {
            writer.Write("null");
            return;
        }

        var value = node.GetValue<object?>();
        switch (value)
        {
            case null:
                writer.Write("null");
                break;
            case string text:
                writer.Write(QuoteYamlString(text));
                break;
            case bool boolean:
                writer.Write(boolean ? "true" : "false");
                break;
            default:
                writer.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string QuoteYamlString(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(static character => ":#[]{}&*!|>'\"%@`".Contains(character))
            ? JsonSerializer.Serialize(value)
            : value;

    private static void WriteIndent(TextWriter writer, int indent) =>
        writer.Write(new string(' ', indent));
}
