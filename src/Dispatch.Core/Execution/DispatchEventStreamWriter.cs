using System.Text;
using System.Text.Json;
using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

internal sealed class DispatchEventStreamWriter : IDispatchExecutionObserver, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private readonly StreamWriter writer;
    private readonly object sync = new();

    public DispatchEventStreamWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new StreamWriter(path, append: false, Encoding.UTF8);
    }

    public void WriteRunStarted(ExecutionPlan plan, DateTimeOffset timestamp)
    {
        Write(new
        {
            type = "run.started",
            runId = plan.RunId,
            timestamp,
            transport = plan.Job.Transport,
            payloadType = plan.Job.Payload.PayloadType,
            payloadName = plan.Job.Payload.DisplayName,
            targetCount = plan.Targets.Count,
            details = new
            {
                localRunRoot = plan.LocalRunRoot,
                localAdminRoot = plan.LocalAdminRoot,
                resultsJsonPath = plan.LocalResultsJsonPath,
                eventsNdjsonPath = plan.LocalEventsNdjsonPath
            }
        });
    }

    public void WritePlan(ExecutionPlan plan)
    {
        Write(new
        {
            type = "plan",
            runId = plan.RunId,
            timestamp = DateTimeOffset.UtcNow,
            plan
        });
    }

    public void WriteExecutionStarted(ExecutionPlan plan, DateTimeOffset timestamp)
    {
        Write(new
        {
            type = "execution.started",
            runId = plan.RunId,
            timestamp,
            targetCount = plan.Targets.Count,
            throttleLimit = plan.ThrottleLimit
        });
    }

    public void WriteTargetResult(TargetExecutionResult result)
    {
        Write(new
        {
            type = "target.result",
            runId = result.RunId,
            timestamp = result.EndedAt,
            target = result.Target,
            result
        });
    }

    public void WriteRunResult(DispatchRunResult result)
    {
        Write(new
        {
            type = "result",
            runId = result.RunId,
            timestamp = result.EndedAt,
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
            message = progress.Message
        });

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            writer.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void Write<T>(T value)
    {
        var line = JsonSerializer.Serialize(value, Options);
        lock (sync)
        {
            writer.WriteLine(line);
            writer.Flush();
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
