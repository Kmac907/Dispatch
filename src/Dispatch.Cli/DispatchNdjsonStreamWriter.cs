using System.Text.Json;
using Dispatch.Core;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Core.Redaction;

namespace Dispatch.Cli;

internal sealed class DispatchNdjsonStreamWriter(TextWriter writer, bool verbose, bool trace)
    : IDispatchExecutionObserver
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly object sync = new();

    public void WritePlanningStarted()
    {
        Write(new
        {
            type = "planning.started",
            timestamp = DateTimeOffset.UtcNow,
            verbosity = GetVerbosity()
        });
    }

    public void WritePlan(ExecutionPlan plan)
    {
        Write(new
        {
            type = "plan",
            runId = plan.RunId,
            timestamp = DateTimeOffset.UtcNow,
            plan,
            details = CreatePlanDetails(plan)
        });
    }

    public void WriteExecutionStarted(ExecutionPlan plan)
    {
        Write(new
        {
            type = "execution.started",
            runId = plan.RunId,
            timestamp = DateTimeOffset.UtcNow,
            targetCount = plan.Targets.Count,
            throttleLimit = plan.ThrottleLimit,
            verbosity = GetVerbosity()
        });
    }

    public void WriteResult(DispatchRunResult result)
    {
        Write(new
        {
            type = "result",
            runId = result.RunId,
            timestamp = DateTimeOffset.UtcNow,
            result
        });
    }

    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        Write(new
        {
            type = "progress",
            runId = progress.RunId,
            timestamp = progress.Timestamp,
            target = progress.Target,
            state = progress.State,
            failureCategory = progress.FailureCategory,
            message = progress.Message,
            details = CreateProgressDetails(progress)
        });

        return Task.CompletedTask;
    }

    private object? CreatePlanDetails(ExecutionPlan plan)
    {
        if (!verbose && !trace)
        {
            return null;
        }

        return new
        {
            verbosity = GetVerbosity(),
            transport = plan.Job.Transport,
            payloadType = plan.Job.Payload.PayloadType,
            payloadName = plan.Job.Payload.DisplayName,
            targetCount = plan.Targets.Count,
            localRunRoot = trace ? plan.LocalRunRoot : null,
            remoteRunRoot = trace ? plan.RemoteRunRoot : null,
            resultsJsonPath = trace ? plan.LocalResultsJsonPath : null,
            eventsNdjsonPath = trace ? plan.LocalEventsNdjsonPath : null,
            resultsCsvPath = trace && plan.Job.ResultPolicy.WriteCsv ? plan.LocalResultsCsvPath : null,
            targets = trace
                ? plan.Targets.Select(target => new
                {
                    name = target.Target.Name,
                    source = target.Target.Source,
                    localResultPath = plan.Job.ResultPolicy.WritePerTargetJson ? target.PlannedLocalResultPath : null,
                    remoteScriptPath = target.PlannedRemoteScriptPath
                }).ToArray()
                : null
        };
    }

    private object? CreateProgressDetails(DispatchExecutionProgress progress)
    {
        if (!verbose && !trace)
        {
            return null;
        }

        return new
        {
            verbosity = GetVerbosity(),
            terminal = progress.State is TargetExecutionState.Succeeded
                or TargetExecutionState.Failed
                or TargetExecutionState.TimedOut
                or TargetExecutionState.Cancelled,
            hasFailure = progress.FailureCategory != FailureCategory.None,
            operation = progress.Details?.Operation,
            location = trace ? progress.Details?.Location : null,
            completedUnits = progress.Details?.CompletedUnits,
            totalUnits = progress.Details?.TotalUnits,
            unitLabel = progress.Details?.UnitLabel,
            completedBytes = progress.Details?.CompletedBytes,
            totalBytes = progress.Details?.TotalBytes
        };
    }

    private string GetVerbosity() =>
        trace ? "trace" : verbose ? "verbose" : "normal";

    private void Write<T>(T value)
    {
        lock (sync)
        {
            writer.WriteLine(DispatchRedactor.RedactJson(JsonSerializer.Serialize(value, Options)));
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(DispatchJson.Options)
        {
            WriteIndented = false
        };
        return options;
    }
}
