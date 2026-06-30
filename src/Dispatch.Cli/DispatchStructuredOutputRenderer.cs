using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dispatch.Core;
using Dispatch.Core.Credentials;
using Dispatch.Core.Models;
using Dispatch.Core.Targeting;
using Dispatch.Core.Validation;

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

    public static void RenderApplyPlan(TextWriter writer, DispatchApplyPlan plan, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(plan));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "apply.plan", apply = plan },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, plan);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine($"Apply {plan.Mode}: {plan.Tasks.Count} selected tasks");
                foreach (var task in plan.Tasks)
                {
                    writer.WriteLine($"Task {task.Index}: {task.Type} {task.DisplayValue}");
                    if (task.Plan is not null)
                    {
                        SpectreConsoleRenderer.RenderDryRunPlan(writer, task.Plan);
                    }
                    else if (task.Type.Equals("copy", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteLine($"  Source: {task.SourcePath}");
                        writer.WriteLine($"  Destination: {task.DestinationPath}");
                        writer.WriteLine($"  Overwrite: {task.Overwrite}");
                        writer.WriteLine($"  Transport: {task.Transport?.ToDispatchString()}");
                        writer.WriteLine($"  Targets: {string.Join(", ", task.Targets ?? [])}");
                    }
                }

                break;
        }
    }

    public static void RenderApplyExecution(TextWriter writer, DispatchApplyExecution execution, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(execution));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "apply.execute", apply = execution },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, execution);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine($"Apply {execution.Mode}: {execution.Tasks.Count} executed tasks");
                foreach (var task in execution.Tasks)
                {
                    writer.WriteLine($"Task {task.Index}: {task.Type} {task.DisplayValue}");
                    SpectreConsoleRenderer.RenderRunResult(writer, task.Result);
                }

                break;
        }
    }

    public static void RenderPushPlan(TextWriter writer, DispatchPushPlan plan, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(plan));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "push.plan", push = plan },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, plan);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine("Dispatch push plan");
                writer.WriteLine($"Source: {plan.SourcePath}");
                writer.WriteLine($"Destination: {plan.DestinationPath}");
                writer.WriteLine($"Transport: {plan.Transport.ToDispatchString()}");
                writer.WriteLine($"Targets: {string.Join(", ", plan.TargetNames)}");
                writer.WriteLine($"Overwrite: {plan.Overwrite}");
                writer.WriteLine($"Checksum: {plan.Checksum}");
                writer.WriteLine($"Backup: {plan.Backup}");
                writer.WriteLine($"Execute: {plan.Execute}");
                writer.WriteLine($"Cleanup: {plan.Cleanup}");
                writer.WriteLine($"Concurrency: {plan.Concurrency}");
                break;
        }
    }

    public static void RenderPushResult(TextWriter writer, DispatchPushResult result, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "push.result", push = result },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine(result.Succeeded ? "Dispatch push complete" : "Dispatch push completed with failures");
                writer.WriteLine($"Source: {result.Plan.SourcePath}");
                writer.WriteLine($"Destination: {result.Plan.DestinationPath}");
                writer.WriteLine($"Transport: {result.Plan.Transport.ToDispatchString()}");
                foreach (var target in result.Targets)
                {
                    var status = target.Succeeded ? "succeeded" : "failed";
                    writer.WriteLine($"{target.Target}: {status}; bytes={target.BytesUploaded}; failure={target.FailureMessage ?? "-"}");
                }

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

    public static void RenderRunLogExport(TextWriter writer, DispatchRunLogExportResult result, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderRunLogExport(writer, result);
                break;
        }
    }

    public static void RenderRunRetryPlan(TextWriter writer, DispatchRunRetryPlan retryPlan, DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(retryPlan));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    retryPlan,
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, retryPlan);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderRunRetryPlan(writer, retryPlan);
                break;
        }
    }

    public static void RenderCredentialOperation(
        TextWriter writer,
        CredentialProviderOperationResult result,
        DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                SpectreConsoleRenderer.RenderCredentialOperation(writer, result);
                break;
        }
    }

    public static void RenderHostInventoryInspection(
        TextWriter writer,
        InventoryInspectionResult result,
        DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                foreach (var host in result.Hosts)
                {
                    writer.WriteLine(JsonSerializer.Serialize(
                        new { type = "hosts.host", inventory = result.InventoryPath, host },
                        new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine("Dispatch hosts");
                writer.WriteLine($"Inventory: {result.InventoryPath}");
                writer.WriteLine($"Hosts: {result.Hosts.Count}");
                foreach (var host in result.Hosts)
                {
                    var groups = host.Groups.Count == 0 ? "-" : string.Join(",", host.Groups);
                    var transport = host.Transport?.ToDispatchString() ?? "-";
                    var credential = string.IsNullOrWhiteSpace(host.CredentialReference) ? "-" : host.CredentialReference;
                    var allowPsExecFallback = host.AllowPsExecFallback?.ToString().ToLowerInvariant() ?? "-";
                    writer.WriteLine($"{host.Name} | groups={groups} | transport={transport} | credential={credential} | allow_psexec_fallback={allowPsExecFallback} | source={host.Source}");
                }

                break;
        }
    }

    public static void RenderHostInventoryValidation(
        TextWriter writer,
        InventoryInspectionResult result,
        DispatchOutputMode mode)
    {
        var validation = new DispatchHostInventoryValidation(
            result.InventoryPath,
            result.IsValid,
            result.Hosts.Count,
            result.Errors);
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(validation));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "hosts.validation", validation },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, validation);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine("Dispatch hosts valid");
                writer.WriteLine($"Inventory: {result.InventoryPath}");
                writer.WriteLine($"Hosts: {result.Hosts.Count}");
                break;
        }
    }

    public static void RenderHostInventoryGraph(
        TextWriter writer,
        InventoryGraphInspectionResult result,
        DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                foreach (var group in result.Groups)
                {
                    writer.WriteLine(JsonSerializer.Serialize(
                        new { type = "hosts.graph.group", inventory = result.InventoryPath, group },
                        new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                foreach (var host in result.UngroupedHosts)
                {
                    writer.WriteLine(JsonSerializer.Serialize(
                        new { type = "hosts.graph.ungrouped", inventory = result.InventoryPath, host },
                        new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine("Dispatch hosts graph");
                writer.WriteLine($"Inventory: {result.InventoryPath}");
                writer.WriteLine($"Groups: {result.Groups.Count}");
                writer.WriteLine($"Ungrouped hosts: {result.UngroupedHosts.Count}");
                foreach (var group in result.Groups)
                {
                    var hosts = group.Hosts.Count == 0 ? "-" : string.Join(",", group.Hosts);
                    var children = group.Children.Count == 0 ? "-" : string.Join(",", group.Children);
                    var transport = group.Transport?.ToDispatchString() ?? "-";
                    var credential = string.IsNullOrWhiteSpace(group.CredentialReference) ? "-" : group.CredentialReference;
                    var allowPsExecFallback = group.AllowPsExecFallback?.ToString().ToLowerInvariant() ?? "-";
                    writer.WriteLine($"{group.Name} | children={children} | hosts={hosts} | transport={transport} | credential={credential} | allow_psexec_fallback={allowPsExecFallback}");
                }

                if (result.UngroupedHosts.Count > 0)
                {
                    writer.WriteLine($"ungrouped | hosts={string.Join(",", result.UngroupedHosts)}");
                }

                break;
        }
    }

    public static void RenderHostVars(
        TextWriter writer,
        DispatchHostVarsResult result,
        DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                writer.WriteLine(JsonSerializer.Serialize(
                    new { type = "hosts.vars", inventory = result.InventoryPath, target = result.Target, host = result.Host },
                    new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine("Dispatch host vars");
                writer.WriteLine($"Inventory: {result.InventoryPath}");
                writer.WriteLine($"Target: {result.Host.Name}");
                writer.WriteLine($"Source: {result.Host.Source}");
                writer.WriteLine($"Groups: {(result.Host.Groups.Count == 0 ? "-" : string.Join(",", result.Host.Groups))}");
                writer.WriteLine($"Transport: {result.Host.Transport?.ToDispatchString() ?? "-"}");
                writer.WriteLine($"Credential: {(string.IsNullOrWhiteSpace(result.Host.CredentialReference) ? "-" : result.Host.CredentialReference)}");
                writer.WriteLine($"Allow PsExec fallback: {result.Host.AllowPsExecFallback?.ToString().ToLowerInvariant() ?? "-"}");
                break;
        }
    }

    public static void RenderHostTestResult(
        TextWriter writer,
        DispatchHostTestResult result,
        DispatchOutputMode mode)
    {
        switch (mode)
        {
            case DispatchOutputMode.Json:
                writer.WriteLine(DispatchJson.Serialize(result));
                break;
            case DispatchOutputMode.Ndjson:
                foreach (var target in result.Targets)
                {
                    writer.WriteLine(JsonSerializer.Serialize(
                        new { type = "hosts.test.target", inventory = result.InventoryPath, target },
                        new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = false }));
                }

                break;
            case DispatchOutputMode.Yaml:
                WriteYaml(writer, result);
                break;
            case DispatchOutputMode.Rich:
            case DispatchOutputMode.Table:
            default:
                writer.WriteLine(result.Succeeded ? "Dispatch hosts test passed" : "Dispatch hosts test failed");
                writer.WriteLine($"Inventory: {result.InventoryPath}");
                writer.WriteLine($"Selector: {result.TargetSelector}");
                writer.WriteLine($"Transport: {result.TransportSelector}");
                foreach (var target in result.Targets)
                {
                    var status = target.Succeeded ? "reachable" : "failed";
                    var failure = string.IsNullOrWhiteSpace(target.FailureMessage) ? "-" : target.FailureMessage;
                    writer.WriteLine($"{target.Target} | transport={target.Transport.ToDispatchString()} | status={status} | failure={failure}");
                }

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

internal sealed record DispatchApplyPlan(
    string Mode,
    IReadOnlyList<DispatchApplyPlannedTask> Tasks);

internal sealed record DispatchApplyPlannedTask(
    int Index,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScriptPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CommandLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourcePath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DestinationPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Overwrite,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TransportKind? Transport,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Targets,
    IReadOnlyList<string> Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionPlan? Plan)
{
    internal string DisplayValue
    {
        get
        {
            if (ScriptPath is not null)
            {
                return ScriptPath;
            }

            if (CommandLine is not null)
            {
                return CommandLine;
            }

            return SourcePath is not null && DestinationPath is not null
                ? $"{SourcePath} -> {DestinationPath}"
                : string.Empty;
        }
    }
}

internal sealed record DispatchApplyExecution(
    string Mode,
    IReadOnlyList<DispatchApplyExecutedTask> Tasks);

internal sealed record DispatchApplyExecutedTask(
    int Index,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScriptPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CommandLine,
    IReadOnlyList<string> Tags,
    DispatchRunResult Result)
{
    internal string DisplayValue => ScriptPath ?? CommandLine ?? string.Empty;
}

internal sealed record DispatchRunCommandOutcome(
    int ExitCode,
    DispatchRunResult? Result);

internal sealed record DispatchPushPlan(
    string Mode,
    string SourcePath,
    string DestinationPath,
    long SourceBytes,
    TransportKind Transport,
    IReadOnlyList<TargetSpec> Targets,
    bool Overwrite,
    bool Checksum,
    bool Backup,
    bool Execute,
    bool Cleanup,
    int Concurrency,
    [property: JsonIgnore]
    DispatchOutputMode OutputMode)
{
    public IReadOnlyList<string> TargetNames => Targets.Select(static target => target.Name).ToArray();
}

internal sealed record DispatchPushResult(
    DispatchPushPlan Plan,
    bool Succeeded,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<DispatchPushTargetResult> Targets);

internal sealed record DispatchPushTargetResult(
    string Target,
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    long BytesUploaded,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record DispatchHostInventoryValidation(
    string InventoryPath,
    bool Valid,
    int HostCount,
    IReadOnlyList<DispatchValidationError> Errors);

internal sealed record DispatchHostVarsResult(
    string InventoryPath,
    string Target,
    InventoryHostInspection Host);

internal sealed record DispatchHostTestResult(
    string InventoryPath,
    string TargetSelector,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExcludeSelector,
    string TransportSelector,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<DispatchHostTestTargetResult> Targets)
{
    public bool Succeeded => Targets.All(static target => target.Succeeded);
    public int TargetCount => Targets.Count;
    public int SuccessCount => Targets.Count(static target => target.Succeeded);
    public int FailedCount => Targets.Count(static target => !target.Succeeded);
}

internal sealed record DispatchHostTestTargetResult(
    string Target,
    TransportKind Transport,
    bool Succeeded,
    FailureCategory FailureCategory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyDictionary<string, string> Metadata);
