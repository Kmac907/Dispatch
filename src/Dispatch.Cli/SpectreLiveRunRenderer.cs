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
                await RunDashboardLoopAsync(
                    events.Reader,
                    dashboard.Update,
                    () =>
                    {
                        context.UpdateTarget(dashboard.BuildRenderable());
                        context.Refresh();
                    },
                    cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        await executionTask.ConfigureAwait(false);
        if (executionException is not null)
        {
            throw executionException;
        }

        return result ?? throw new InvalidOperationException("Dispatch execution ended without a run result.");
    }

    internal static async Task RunDashboardLoopAsync(
        ChannelReader<DispatchExecutionProgress> reader,
        Action<DispatchExecutionProgress> onProgress,
        Action refresh,
        CancellationToken cancellationToken)
    {
        refresh();

        while (true)
        {
            while (reader.TryRead(out var progress))
            {
                onProgress(progress);
            }

            refresh();

            var waitForEvent = reader.WaitToReadAsync(cancellationToken).AsTask();
            var waitForHeartbeat = Task.Delay(HeartbeatInterval, cancellationToken);
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

        refresh();
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
            Details = progress.Details,
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
                Details = existing.Details,
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
            CreateOutputLocationsPanel(),
            CreateCompletionPanel(),
            CreatePhaseSummaryPanel(),
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

    private IRenderable CreatePhaseSummaryPanel()
    {
        var phaseGroups = targets.Values
            .GroupBy(target => FormatPhase(target.State))
            .Select(group => new { Phase = group.Key, Count = group.Count(), SortOrder = GetPhaseSortOrder(group.First().State) })
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Phase, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Phase");
        table.AddColumn("Count");

        foreach (var phase in phaseGroups)
        {
            table.AddRow(Markup.Escape(phase.Phase), phase.Count.ToString());
        }

        return new Panel(table)
            .Header("Phase Counts")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IRenderable CreateOutputLocationsPanel()
    {
        var targetRootPattern = Path.Combine(plan.LocalRunRoot, "Targets", "<target>");
        var lines = new[]
        {
            $"[bold]Results[/]: {Markup.Escape(plan.LocalResultsJsonPath)}",
            $"[bold]Events[/]: {Markup.Escape(plan.LocalEventsNdjsonPath ?? "-")}",
            $"[bold]Target Root[/]: {Markup.Escape(targetRootPattern)}",
            $"[bold]Stdout/Stderr[/]: {Markup.Escape(Path.Combine(targetRootPattern, "stdout.txt"))} / {Markup.Escape(Path.Combine(targetRootPattern, "stderr.txt"))}"
        };

        return new Panel(new Rows(lines.Select(static line => (IRenderable)new Markup(line)).ToArray()))
            .Header("Outputs")
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
        table.AddColumn("Phase Elapsed");
        table.AddColumn("Progress");
        table.AddColumn("Exit");
        table.AddColumn("Detail");

        foreach (var target in OrderTargets())
        {
            table.AddRow(
                Markup.Escape(target.Name),
                FormatStatusMarkup(target.State),
                Markup.Escape(FormatPhase(target.State)),
                Markup.Escape(FormatPhaseElapsed(target, now)),
                Markup.Escape(FormatMeasuredProgress(target)),
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

    private static string FormatPhaseElapsed(TargetProgress target, DateTimeOffset now)
    {
        if (target.StateStartedAt is null)
        {
            return "-";
        }

        return FormatElapsed(target.StateStartedAt.Value, target.EndedAt ?? now);
    }

    private static string FormatMeasuredProgress(TargetProgress target)
    {
        if (target.Details is null)
        {
            return "-";
        }

        if (target.Details.TotalUnits is > 0 && target.Details.CompletedUnits is not null)
        {
            var percent = (int)Math.Round((double)target.Details.CompletedUnits.Value / target.Details.TotalUnits.Value * 100, MidpointRounding.AwayFromZero);
            var filled = Math.Clamp((int)Math.Round(percent / 10d, MidpointRounding.AwayFromZero), 0, 10);
            var unitLabel = string.IsNullOrWhiteSpace(target.Details.UnitLabel) ? "items" : target.Details.UnitLabel;
            return $"{target.Details.Operation ?? "Progress"} [{new string('#', filled).PadRight(10, '-')}] {target.Details.CompletedUnits}/{target.Details.TotalUnits} {unitLabel}";
        }

        if (target.Details.TotalBytes is > 0 && target.Details.CompletedBytes is not null)
        {
            var percent = (int)Math.Round((double)target.Details.CompletedBytes.Value / target.Details.TotalBytes.Value * 100, MidpointRounding.AwayFromZero);
            var filled = Math.Clamp((int)Math.Round(percent / 10d, MidpointRounding.AwayFromZero), 0, 10);
            return $"{target.Details.Operation ?? "Progress"} [{new string('#', filled).PadRight(10, '-')}] {FormatBytes(target.Details.CompletedBytes.Value)} / {FormatBytes(target.Details.TotalBytes.Value)}";
        }

        return "-";
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

        if (!string.IsNullOrWhiteSpace(target.Details?.Location))
        {
            detailParts.Add(target.Details.Location);
        }

        if (target.TargetStartedAt is not null)
        {
            detailParts.Add($"Active {FormatElapsed(target.TargetStartedAt.Value, target.EndedAt ?? now)}");
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

    private IEnumerable<TargetProgress> OrderTargets() =>
        targets.Values
            .OrderBy(GetTargetPriority)
            .ThenBy(target => target.StateStartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(target => target.Name, StringComparer.OrdinalIgnoreCase);

    private static int GetTargetPriority(TargetProgress target) =>
        target.State switch
        {
            TargetExecutionState.Executing => 0,
            TargetExecutionState.CollectingArtifacts => 1,
            TargetExecutionState.PreparingScript => 2,
            TargetExecutionState.Probing => 3,
            TargetExecutionState.Resolving => 4,
            TargetExecutionState.Failed => 5,
            TargetExecutionState.TimedOut => 6,
            TargetExecutionState.Cancelled => 7,
            TargetExecutionState.Pending => 8,
            TargetExecutionState.Succeeded => 9,
            _ => 10
        };

    private static int GetPhaseSortOrder(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Executing => 0,
            TargetExecutionState.CollectingArtifacts => 1,
            TargetExecutionState.PreparingScript => 2,
            TargetExecutionState.Probing => 3,
            TargetExecutionState.Resolving => 4,
            TargetExecutionState.Pending => 5,
            TargetExecutionState.Failed => 6,
            TargetExecutionState.TimedOut => 7,
            TargetExecutionState.Cancelled => 8,
            TargetExecutionState.Succeeded => 9,
            _ => 10
        };

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

    private static string FormatBytes(long bytes)
    {
        const long kilobyte = 1024;
        const long megabyte = kilobyte * 1024;

        return bytes switch
        {
            >= megabyte => $"{bytes / (double)megabyte:0.0} MB",
            >= kilobyte => $"{bytes / (double)kilobyte:0.0} KB",
            _ => $"{bytes} B"
        };
    }

    private sealed record TargetProgress(
        string Name,
        TargetExecutionState State,
        FailureCategory FailureCategory,
        string? Message,
        DispatchExecutionProgressDetails? Details,
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
