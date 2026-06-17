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
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
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
                using var heartbeat = new PeriodicTimer(HeartbeatInterval);
                context.UpdateTarget(dashboard.BuildRenderable());
                context.Refresh();

                while (true)
                {
                    while (events.Reader.TryRead(out var progress))
                    {
                        dashboard.Update(progress);
                    }

                    context.UpdateTarget(dashboard.BuildRenderable());
                    context.Refresh();

                    var waitForEvent = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var waitForHeartbeat = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(waitForEvent, waitForHeartbeat).ConfigureAwait(false);
                    if (completed == waitForHeartbeat)
                    {
                        continue;
                    }

                    if (!await waitForEvent.ConfigureAwait(false))
                    {
                        break;
                    }
                }

                context.UpdateTarget(dashboard.BuildRenderable());
                context.Refresh();
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
    private readonly Func<DateTimeOffset> nowProvider;
    private readonly Dictionary<string, TargetProgress> targets;
    private readonly Queue<DispatchExecutionProgress> recentEvents = new();
    private DispatchRunResult? result;

    public SpectreRunDashboard(
        ExecutionPlan plan,
        DateTimeOffset startedAt,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.plan = plan;
        this.startedAt = startedAt;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        targets = plan.Targets.ToDictionary(
            static target => target.Target.Name,
            static target => new TargetProgress(
                target.Target.Name,
                target.State,
                target.FailureCategory,
                target.FailureMessage,
                null,
                null,
                null,
                null));
    }

    public void Update(DispatchExecutionProgress progress)
    {
        var existing = targets[progress.Target];
        var targetStartedAt = existing.TargetStartedAt ?? GetTargetStartedAt(progress.State, progress.Timestamp);
        var stateStartedAt = progress.State == existing.State && existing.StateStartedAt is not null
            ? existing.StateStartedAt
            : GetStateStartedAt(progress.State, progress.Timestamp);
        DateTimeOffset? endedAt = IsTerminal(progress.State) ? progress.Timestamp : null;

        targets[progress.Target] = existing with
        {
            State = progress.State,
            FailureCategory = progress.FailureCategory,
            Message = progress.Message,
            StateStartedAt = stateStartedAt,
            TargetStartedAt = targetStartedAt,
            LastUpdatedAt = progress.Timestamp,
            EndedAt = endedAt
        };

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
            var existing = targets[target.Target];
            var targetStartedAt = existing.TargetStartedAt ?? target.StartedAt;
            var stateStartedAt = existing.State == target.State && existing.StateStartedAt is not null
                ? existing.StateStartedAt
                : target.EndedAt;

            targets[target.Target] = existing with
            {
                State = target.State,
                FailureCategory = target.FailureCategory,
                Message = target.FailureMessage,
                StateStartedAt = stateStartedAt,
                TargetStartedAt = targetStartedAt,
                LastUpdatedAt = target.EndedAt,
                EndedAt = target.EndedAt,
                ExitCode = target.ExitCode
            };
        }
    }

    public IRenderable BuildRenderable()
    {
        var now = nowProvider();
        var rows = new List<IRenderable>
        {
            new Rule($"[bold]Dispatch Run[/] [grey]{Markup.Escape(plan.RunId)}[/]"),
            CreateSummaryTable(now),
            CreateCompletionPanel(),
            CreateOutcomeChart(),
            CreateTargetTable(now),
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

    private Table CreateSummaryTable(DateTimeOffset now)
    {
        var counts = GetCounts();
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Run");
        table.AddColumn("Transport");
        table.AddColumn("Payload");
        table.AddColumn("Targets");
        table.AddColumn("Running");
        table.AddColumn("Succeeded");
        table.AddColumn("Failed");
        table.AddColumn("Pending");
        table.AddColumn("Elapsed");
        table.AddRow(
            Markup.Escape(plan.RunId),
            Markup.Escape(plan.Job.Transport.ToDispatchString()),
            Markup.Escape(plan.Job.Payload.DisplayName),
            plan.Targets.Count.ToString(),
            counts.Active.ToString(),
            counts.Succeeded.ToString(),
            $"{counts.Failed + counts.TimedOut + counts.Cancelled}",
            counts.Pending.ToString(),
            FormatElapsed(startedAt, result?.EndedAt ?? now));

        return table;
    }

    private IRenderable CreateCompletionPanel()
    {
        var counts = GetCounts();
        var total = Math.Max(1, plan.Targets.Count);
        var percent = (int)Math.Round((double)counts.Complete / total * 100, MidpointRounding.AwayFromZero);
        var filled = Math.Clamp((int)Math.Round(percent / 5d, MidpointRounding.AwayFromZero), 0, 20);
        var bar = $"[{new string('#', filled).PadRight(20, '-')}] {counts.Complete}/{plan.Targets.Count} ({percent}%)";
        var details =
            $"[bold]{Markup.Escape(bar)}[/]{Environment.NewLine}" +
            $"[grey]{counts.Active} running, {counts.Pending} pending, {counts.Succeeded} succeeded, {counts.Failed} failed, {counts.TimedOut} timed out, {counts.Cancelled} cancelled[/]";

        return new Panel(new Markup(details))
            .Header("Completion")
            .Border(BoxBorder.Rounded)
            .Expand();
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

    private Table CreateTargetTable(DateTimeOffset now)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Target");
        table.AddColumn("Status");
        table.AddColumn("Phase");
        table.AddColumn("Elapsed");
        table.AddColumn("Exit");
        table.AddColumn("Detail");

        foreach (var target in targets.Values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                Markup.Escape(target.Name),
                FormatStatusMarkup(target.State),
                Markup.Escape(FormatPhase(target.State)),
                Markup.Escape(FormatTargetElapsed(target, now)),
                Markup.Escape(target.ExitCode?.ToString() ?? "-"),
                Markup.Escape(FormatDetail(target, now)));
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
                Markup.Escape(FormatPhase(progress.State)),
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

    private static string FormatStatusMarkup(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]Succeeded[/]",
            TargetExecutionState.Failed => "[red]Failed[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed Out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            TargetExecutionState.Pending => "[grey]Pending[/]",
            _ => "[blue]Running[/]"
        };

    private static string FormatPhase(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Pending => "Pending",
            TargetExecutionState.Resolving => "Resolving",
            TargetExecutionState.Probing => "Probing",
            TargetExecutionState.PreparingScript => "Preparing Script",
            TargetExecutionState.Executing => "Executing",
            TargetExecutionState.CollectingArtifacts => "Collecting Artifacts",
            _ => "Complete"
        };

    private static string FormatTargetElapsed(TargetProgress target, DateTimeOffset now)
    {
        if (target.TargetStartedAt is null)
        {
            return "-";
        }

        return FormatElapsed(target.TargetStartedAt.Value, target.EndedAt ?? now);
    }

    private static string FormatDetail(TargetProgress target, DateTimeOffset now)
    {
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.Message))
        {
            detailParts.Add(target.FailureCategory == FailureCategory.None
                ? target.Message
                : $"{target.FailureCategory}: {target.Message}");
        }

        if (target.StateStartedAt is not null && !IsTerminal(target.State))
        {
            detailParts.Add($"Phase {FormatElapsed(target.StateStartedAt.Value, now)}");
        }

        if (detailParts.Count > 0)
        {
            return string.Join(" | ", detailParts);
        }

        return target.LastUpdatedAt is null ? "-" : target.LastUpdatedAt.Value.ToLocalTime().ToString("HH:mm:ss");
    }

    private static DateTimeOffset? GetStateStartedAt(TargetExecutionState state, DateTimeOffset timestamp) =>
        state == TargetExecutionState.Pending ? null : timestamp;

    private static DateTimeOffset? GetTargetStartedAt(TargetExecutionState state, DateTimeOffset timestamp) =>
        state == TargetExecutionState.Pending ? null : timestamp;

    private static bool IsTerminal(TargetExecutionState state) =>
        state is TargetExecutionState.Succeeded
            or TargetExecutionState.Failed
            or TargetExecutionState.TimedOut
            or TargetExecutionState.Cancelled;

    private static string FormatElapsed(DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var elapsed = endedAt - startedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private sealed record TargetProgress(
        string Name,
        TargetExecutionState State,
        FailureCategory FailureCategory,
        string? Message,
        DateTimeOffset? StateStartedAt,
        DateTimeOffset? TargetStartedAt,
        DateTimeOffset? LastUpdatedAt,
        DateTimeOffset? EndedAt,
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
