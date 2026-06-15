using System.Threading.Channels;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dispatch.Cli;

internal sealed class SpectreLiveRunRenderer(
    ExecutionPlan plan,
    IDispatchExecutor executor,
    TextWriter writer,
    bool useLiveDisplay,
    bool noColor = false)
{
    private readonly SpectreRunDashboard dashboard = new(plan, DateTimeOffset.UtcNow);

    public async Task<DispatchRunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!useLiveDisplay)
        {
            return await ExecuteAppendOnlyAsync(cancellationToken).ConfigureAwait(false);
        }

        var console = SpectreConsoleRenderer.CreateInteractiveConsole(writer, noColor);
        var events = Channel.CreateUnbounded<DispatchExecutionProgress>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        var observer = new ChannelDispatchExecutionObserver(events.Writer);

        DispatchRunResult? result = null;
        Exception? executionException = null;
        var executionTask = Task.Run(
            async () =>
            {
                try
                {
                    result = await executor.ExecuteAsync(plan, observer, cancellationToken).ConfigureAwait(false);
                    dashboard.Complete(result);
                }
                catch (Exception exception)
                {
                    executionException = exception;
                }
                finally
                {
                    events.Writer.TryComplete(executionException);
                }
            },
            CancellationToken.None);

        await console
            .Live(dashboard.BuildRenderable())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async context =>
            {
                context.UpdateTarget(dashboard.BuildRenderable());
                context.Refresh();

                await foreach (var progress in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    dashboard.Update(progress);
                    context.UpdateTarget(dashboard.BuildRenderable());
                    context.Refresh();
                }
            })
            .ConfigureAwait(false);

        await executionTask.ConfigureAwait(false);
        if (executionException is not null)
        {
            throw executionException;
        }

        return result ?? throw new InvalidOperationException("Dispatch execution ended without a run result.");
    }

    private async Task<DispatchRunResult> ExecuteAppendOnlyAsync(CancellationToken cancellationToken)
    {
        var observer = new AppendOnlyDispatchExecutionObserver(dashboard, writer);
        var result = await executor.ExecuteAsync(plan, observer, cancellationToken).ConfigureAwait(false);
        dashboard.Complete(result);
        dashboard.RenderSnapshot(writer);
        return result;
    }

    private sealed class ChannelDispatchExecutionObserver(ChannelWriter<DispatchExecutionProgress> writer)
        : IDispatchExecutionObserver
    {
        public async Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken) =>
            await writer.WriteAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    private sealed class AppendOnlyDispatchExecutionObserver(SpectreRunDashboard dashboard, TextWriter writer)
        : IDispatchExecutionObserver
    {
        public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
        {
            dashboard.Update(progress);
            writer.WriteLine($"{progress.Timestamp:HH:mm:ss} {progress.Target} {FormatState(progress.State)}");
            return Task.CompletedTask;
        }
    }

    private static string FormatState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.PreparingScript => "preparing script",
            TargetExecutionState.CollectingArtifacts => "collecting artifacts",
            _ => state.ToString().ToLowerInvariant()
        };
}

internal sealed class SpectreRunDashboard
{
    private readonly ExecutionPlan plan;
    private readonly DateTimeOffset startedAt;
    private readonly Dictionary<string, TargetProgress> targets;
    private readonly Queue<DispatchExecutionProgress> recentEvents = new();
    private DispatchRunResult? result;

    public SpectreRunDashboard(ExecutionPlan plan, DateTimeOffset startedAt)
    {
        this.plan = plan;
        this.startedAt = startedAt;
        targets = plan.Targets.ToDictionary(
            static target => target.Target.Name,
            static target => new TargetProgress(
                target.Target.Name,
                target.State,
                target.FailureCategory,
                target.FailureMessage,
                null));
    }

    public void Update(DispatchExecutionProgress progress)
    {
        targets[progress.Target] = new TargetProgress(
            progress.Target,
            progress.State,
            progress.FailureCategory,
            progress.Message,
            progress.Timestamp);
        recentEvents.Enqueue(progress);
        while (recentEvents.Count > 8)
        {
            recentEvents.Dequeue();
        }
    }

    public void Complete(DispatchRunResult runResult)
    {
        result = runResult;
        foreach (var target in runResult.Targets)
        {
            targets[target.Target] = new TargetProgress(
                target.Target,
                target.State,
                target.FailureCategory,
                target.FailureMessage,
                target.EndedAt,
                target.ExitCode);
        }
    }

    public IRenderable BuildRenderable()
    {
        var rows = new List<IRenderable>
        {
            new Rule($"[bold]Dispatch Run[/] [grey]{Markup.Escape(plan.RunId)}[/]"),
            CreateSummaryTable(),
            CreateOutcomeChart(),
            CreateTargetTable(),
            CreateRecentEventsTable()
        };

        return new Rows(rows);
    }

    public void RenderSnapshot(TextWriter writer)
    {
        var console = SpectreConsoleRenderer.CreateConsole(writer);
        console.Write(BuildRenderable());
        console.WriteLine();
    }

    private Table CreateSummaryTable()
    {
        var counts = GetCounts();
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Run");
        table.AddColumn("Transport");
        table.AddColumn("Payload");
        table.AddColumn("Targets");
        table.AddColumn("Active");
        table.AddColumn("Complete");
        table.AddColumn("Elapsed");
        table.AddRow(
            Markup.Escape(plan.RunId),
            Markup.Escape(plan.Job.Transport.ToDispatchString()),
            Markup.Escape(plan.Job.Payload.DisplayName),
            plan.Targets.Count.ToString(),
            counts.Active.ToString(),
            $"{counts.Complete}/{plan.Targets.Count}",
            FormatElapsed());

        return table;
    }

    private IRenderable CreateOutcomeChart()
    {
        var counts = GetCounts();
        var chart = new BreakdownChart()
            .Width(80)
            .ShowTags()
            .ShowTagValues()
            .AddItem("Succeeded", Math.Max(0, counts.Succeeded), Color.Green)
            .AddItem("Failed", Math.Max(0, counts.Failed), Color.Red)
            .AddItem("Timed out", Math.Max(0, counts.TimedOut), Color.Yellow)
            .AddItem("Cancelled", Math.Max(0, counts.Cancelled), Color.Grey)
            .AddItem("Running", Math.Max(0, counts.Active), Color.Blue)
            .AddItem("Pending", Math.Max(0, counts.Pending), Color.Grey);

        return new Panel(chart)
            .Header("Outcome Chart")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private Table CreateTargetTable()
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Target");
        table.AddColumn("State");
        table.AddColumn("Progress");
        table.AddColumn("Exit");
        table.AddColumn("Detail");

        foreach (var target in targets.Values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                Markup.Escape(target.Name),
                FormatStateMarkup(target.State),
                Markup.Escape(FormatProgressBar(target.State)),
                Markup.Escape(target.ExitCode?.ToString() ?? "-"),
                Markup.Escape(FormatDetail(target)));
        }

        return table;
    }

    private Table CreateRecentEventsTable()
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Time");
        table.AddColumn("Target");
        table.AddColumn("Event");
        table.AddColumn("Message");

        foreach (var progress in recentEvents.Reverse())
        {
            table.AddRow(
                progress.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                Markup.Escape(progress.Target),
                FormatStateMarkup(progress.State),
                Markup.Escape(progress.Message ?? "-"));
        }

        if (recentEvents.Count == 0)
        {
            table.AddRow("-", "-", "Pending", "Waiting for target events.");
        }

        return table;
    }

    private RunCounts GetCounts()
    {
        var values = targets.Values.ToArray();
        var succeeded = values.Count(static target => target.State == TargetExecutionState.Succeeded);
        var failed = values.Count(static target => target.State == TargetExecutionState.Failed);
        var timedOut = values.Count(static target => target.State == TargetExecutionState.TimedOut);
        var cancelled = values.Count(static target => target.State == TargetExecutionState.Cancelled);
        var pending = values.Count(static target => target.State == TargetExecutionState.Pending);
        var complete = succeeded + failed + timedOut + cancelled;
        var active = Math.Max(0, values.Length - pending - complete);
        return new RunCounts(succeeded, failed, timedOut, cancelled, pending, active, complete);
    }

    private string FormatElapsed()
    {
        var end = result?.EndedAt ?? DateTimeOffset.UtcNow;
        var elapsed = end - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private static string FormatStateMarkup(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]Succeeded[/]",
            TargetExecutionState.Failed => "[red]Failed[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed Out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            TargetExecutionState.Pending => "[grey]Pending[/]",
            TargetExecutionState.Executing => "[blue]Executing[/]",
            _ => $"[blue]{Markup.Escape(FormatStateText(state))}[/]"
        };

    private static string FormatStateText(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.PreparingScript => "Preparing Script",
            TargetExecutionState.CollectingArtifacts => "Collecting Artifacts",
            _ => state.ToString()
        };

    private static string FormatProgressBar(TargetExecutionState state)
    {
        var percent = GetPercentForState(state);
        var filled = Math.Clamp(percent / 5, 0, 20);
        return $"[{new string('#', filled).PadRight(20, '-')}] {percent,3}%";
    }

    private static int GetPercentForState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Pending => 0,
            TargetExecutionState.Resolving => 10,
            TargetExecutionState.Probing => 20,
            TargetExecutionState.PreparingScript => 40,
            TargetExecutionState.Executing => 65,
            TargetExecutionState.CollectingArtifacts => 85,
            TargetExecutionState.Succeeded => 100,
            TargetExecutionState.Failed => 100,
            TargetExecutionState.TimedOut => 100,
            TargetExecutionState.Cancelled => 100,
            _ => 0
        };

    private static string FormatDetail(TargetProgress target)
    {
        if (!string.IsNullOrWhiteSpace(target.Message))
        {
            return target.FailureCategory == FailureCategory.None
                ? target.Message
                : $"{target.FailureCategory}: {target.Message}";
        }

        return target.Timestamp is null ? "-" : target.Timestamp.Value.ToLocalTime().ToString("HH:mm:ss");
    }

    private sealed record TargetProgress(
        string Name,
        TargetExecutionState State,
        FailureCategory FailureCategory,
        string? Message,
        DateTimeOffset? Timestamp,
        int? ExitCode = null);

    private sealed record RunCounts(
        int Succeeded,
        int Failed,
        int TimedOut,
        int Cancelled,
        int Pending,
        int Active,
        int Complete);
}
