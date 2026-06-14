using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dispatch.Cli;

internal sealed class SpectreDispatchRunDashboard
{
    private const int RecentEventLimit = 8;
    private readonly Dictionary<string, TargetStatus> targets;
    private readonly Queue<string> recentEvents = new();
    private readonly ExecutionPlan plan;
    private readonly DateTimeOffset startedAt;
    private DispatchRunResult? completedResult;

    public SpectreDispatchRunDashboard(ExecutionPlan plan, DateTimeOffset startedAt)
    {
        this.plan = plan;
        this.startedAt = startedAt;
        targets = plan.Targets.ToDictionary(
            static target => target.Target.Name,
            static target => new TargetStatus(target.Target.Name, TargetExecutionState.Pending, null, FailureCategory.None, null));
    }

    public void Update(DispatchExecutionProgress progress)
    {
        targets[progress.Target] = new TargetStatus(
            progress.Target,
            progress.State,
            progress.Timestamp,
            progress.FailureCategory,
            progress.Message);

        AddRecentEvent(progress);
    }

    public void Complete(DispatchRunResult result)
    {
        completedResult = result;
        foreach (var target in result.Targets)
        {
            targets[target.Target] = new TargetStatus(
                target.Target,
                target.State,
                target.EndedAt,
                target.FailureCategory,
                target.FailureMessage,
                target.ExitCode,
                target.DurationMs);
        }
    }

    public IRenderable Render()
    {
        var rows = new Rows(
            CreateRunHeader(),
            CreateRunCharts(),
            CreateSummaryTable(),
            CreateTargetTable(),
            CreateActivityPanel(),
            CreateFailurePanel());

        return new Panel(rows)
            .Header("[bold steelblue1] Dispatch Run [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(GetRunBorderColor())
            .Expand();
    }

    private IRenderable CreateRunHeader()
    {
        var table = new Table()
            .NoBorder()
            .HideHeaders()
            .Expand();
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddColumn("Setting");
        table.AddColumn("Value");

        var elapsed = completedResult is null
            ? DateTimeOffset.UtcNow - startedAt
            : completedResult.EndedAt - completedResult.StartedAt;

        table.AddRow(
            "[grey]Run ID[/]",
            Markup.Escape(plan.RunId),
            "[grey]Transport[/]",
            Markup.Escape(plan.Job.Transport.ToString()));
        table.AddRow(
            "[grey]Payload[/]",
            Markup.Escape(plan.Job.Payload.DisplayName),
            "[grey]Targets[/]",
            plan.Targets.Count.ToString());
        table.AddRow(
            "[grey]Elapsed[/]",
            FormatDuration(elapsed),
            "[grey]Results[/]",
            Markup.Escape(string.IsNullOrWhiteSpace(plan.LocalResultsJsonPath)
                ? plan.Job.ResultPolicy.LocalRunRoot
                : plan.LocalResultsJsonPath));

        return table;
    }

    private IRenderable CreateRunCharts()
    {
        var values = targets.Values.ToArray();
        var terminalCount = values.Count(static target => target.State is
            TargetExecutionState.Succeeded or
            TargetExecutionState.Failed or
            TargetExecutionState.TimedOut or
            TargetExecutionState.Cancelled);
        var activeCount = values.Count(static target => target.IsActive);
        var pendingCount = values.Length - terminalCount - activeCount;

        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();

        var phaseChart = new BreakdownChart()
            .Width(60)
            .AddItem("Complete", Math.Max(terminalCount, 0), terminalCount == 0 ? Color.Grey : Color.Green)
            .AddItem("Active", Math.Max(activeCount, 0), activeCount == 0 ? Color.Grey : Color.SteelBlue1)
            .AddItem("Queued", Math.Max(pendingCount, 0), pendingCount == 0 ? Color.Grey : Color.Yellow);

        var outcomeChart = new BarChart()
            .Width(60)
            .Label("[grey]Outcome[/]")
            .CenterLabel();
        outcomeChart.AddItem("Succeeded", values.Count(static target => target.State == TargetExecutionState.Succeeded), Color.Green);
        outcomeChart.AddItem("Failed", values.Count(static target => target.State == TargetExecutionState.Failed), Color.Red);
        outcomeChart.AddItem("Timed Out", values.Count(static target => target.State == TargetExecutionState.TimedOut), Color.Yellow);
        outcomeChart.AddItem("Cancelled", values.Count(static target => target.State == TargetExecutionState.Cancelled), Color.Grey);

        grid.AddRow(phaseChart, outcomeChart);
        return grid;
    }

    private IRenderable CreateSummaryTable()
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .Expand();
        table.AddColumn("Total");
        table.AddColumn("[steelblue1]Active[/]");
        table.AddColumn("[green]Succeeded[/]");
        table.AddColumn("[red]Failed[/]");
        table.AddColumn("[yellow]Timed Out[/]");
        table.AddColumn("[grey]Cancelled[/]");
        table.AddColumn("[grey]Pending[/]");

        var values = targets.Values.ToArray();
        table.AddRow(
            values.Length.ToString(),
            values.Count(static target => target.IsActive).ToString(),
            values.Count(static target => target.State == TargetExecutionState.Succeeded).ToString(),
            values.Count(static target => target.State == TargetExecutionState.Failed).ToString(),
            values.Count(static target => target.State == TargetExecutionState.TimedOut).ToString(),
            values.Count(static target => target.State == TargetExecutionState.Cancelled).ToString(),
            values.Count(static target => target.State is TargetExecutionState.Pending or TargetExecutionState.Resolving).ToString());

        return table;
    }

    private IRenderable CreateTargetTable()
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .Expand();
        table.AddColumn("Target");
        table.AddColumn("Status");
        table.AddColumn("Phase");
        table.AddColumn("Exit");
        table.AddColumn("Duration");
        table.AddColumn("Detail");

        foreach (var target in targets.Values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                Markup.Escape(target.Name),
                FormatStatusSymbol(target.State),
                FormatState(target.State),
                target.ExitCode?.ToString() ?? "-",
                target.DurationMs.HasValue ? FormatDuration(TimeSpan.FromMilliseconds(target.DurationMs.Value)) : "-",
                FormatStatus(target));
        }

        return table;
    }

    private IRenderable CreateActivityPanel()
    {
        if (recentEvents.Count == 0)
        {
            return new Panel(new Markup("[grey]Waiting for execution progress...[/]"))
                .Header("[grey] Recent Activity [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey);
        }

        return new Panel(new Rows(recentEvents.Select(static item => new Markup(item)).ToArray()))
            .Header("[grey] Recent Activity [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    private IRenderable CreateFailurePanel()
    {
        var failures = targets.Values
            .Where(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled)
            .OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (failures.Length == 0)
        {
            return new Panel(new Markup("[green]No target failures reported.[/]"))
                .Header("[grey] Failures [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey);
        }

        var table = new Table()
            .NoBorder()
            .Expand();
        table.AddColumn("Target");
        table.AddColumn("Category");
        table.AddColumn("Message");

        foreach (var failure in failures.Take(5))
        {
            table.AddRow(
                Markup.Escape(failure.Name),
                Markup.Escape(failure.FailureCategory.ToString()),
                Markup.Escape(Trim(failure.Message ?? "No failure message.", 96)));
        }

        if (failures.Length > 5)
        {
            table.AddRow("[grey]...[/]", "[grey]More[/]", Markup.Escape($"{failures.Length - 5} additional failures in results.json"));
        }

        return new Panel(table)
            .Header("[red] Failures [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red);
    }

    private void AddRecentEvent(DispatchExecutionProgress progress)
    {
        var color = GetStateColor(progress.State);
        var message = progress.State == TargetExecutionState.Failed && !string.IsNullOrWhiteSpace(progress.Message)
            ? $" - {Trim(progress.Message, 80)}"
            : string.Empty;

        recentEvents.Enqueue(
            $"[grey]{progress.Timestamp:HH:mm:ss}[/] {FormatStatusSymbol(progress.State)} {Markup.Escape(progress.Target)} [{color}]{Markup.Escape(FormatStateText(progress.State))}[/]{Markup.Escape(message)}");
        while (recentEvents.Count > RecentEventLimit)
        {
            recentEvents.Dequeue();
        }
    }

    private Color GetRunBorderColor()
    {
        if (completedResult is null)
        {
            return Color.SteelBlue1;
        }

        return completedResult.FailedCount == 0 && completedResult.TimedOutCount == 0 && completedResult.CancelledCount == 0
            ? Color.Green
            : Color.Red;
    }

    private static string FormatStatus(TargetStatus target) =>
        target.State switch
        {
            TargetExecutionState.Succeeded => "[green]Complete[/]",
            TargetExecutionState.Failed => $"[red]{Markup.Escape(target.FailureCategory.ToString())}[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            TargetExecutionState.Pending => "[grey]Queued[/]",
            TargetExecutionState.Resolving => "[steelblue1]Resolving[/]",
            TargetExecutionState.Probing => "[steelblue1]Checking access[/]",
            TargetExecutionState.PreparingScript => "[steelblue1]Preparing script[/]",
            TargetExecutionState.Executing => "[steelblue1]Running script[/]",
            TargetExecutionState.CollectingArtifacts => "[steelblue1]Collecting artifacts[/]",
            _ => Markup.Escape(target.State.ToString())
        };

    private static string FormatStatusSymbol(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]✓[/]",
            TargetExecutionState.Failed => "[red]×[/]",
            TargetExecutionState.TimedOut => "[yellow]![/]",
            TargetExecutionState.Cancelled => "[grey]−[/]",
            TargetExecutionState.Pending => "[grey]○[/]",
            _ => "[steelblue1]●[/]"
        };

    private static string FormatState(TargetExecutionState state)
    {
        var color = GetStateColor(state);
        return $"[{color}]{Markup.Escape(FormatStateText(state))}[/]";
    }

    private static string FormatStateText(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.PreparingScript => "Preparing Script",
            TargetExecutionState.CollectingArtifacts => "Collecting Artifacts",
            _ => state.ToString()
        };

    private static string GetStateColor(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "green",
            TargetExecutionState.Failed => "red",
            TargetExecutionState.TimedOut => "yellow",
            TargetExecutionState.Cancelled => "grey",
            TargetExecutionState.Pending => "grey",
            _ => "steelblue1"
        };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 3), "...");

    private sealed record TargetStatus(
        string Name,
        TargetExecutionState State,
        DateTimeOffset? LastUpdatedAt,
        FailureCategory FailureCategory,
        string? Message,
        int? ExitCode = null,
        long? DurationMs = null)
    {
        public bool IsActive => State is
            TargetExecutionState.Resolving or
            TargetExecutionState.Probing or
            TargetExecutionState.PreparingScript or
            TargetExecutionState.Executing or
            TargetExecutionState.CollectingArtifacts;
    }
}
