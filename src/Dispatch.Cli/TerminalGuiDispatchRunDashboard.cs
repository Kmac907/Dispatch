using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Terminal.Gui;

namespace Dispatch.Cli;

internal sealed class TerminalGuiDispatchRunDashboard
{
    private const int RecentEventLimit = 8;
    private readonly Dictionary<string, TargetStatus> targets;
    private readonly Queue<string> recentEvents = new();
    private readonly ExecutionPlan plan;
    private readonly DateTimeOffset startedAt;
    private DispatchRunResult? completedResult;

    public TerminalGuiDispatchRunDashboard(ExecutionPlan plan, DateTimeOffset startedAt)
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

    public Window BuildView()
    {
        var root = new Window("Dispatch Run Dashboard")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        root.Add(CreateRunHeader());
        root.Add(CreateProgressFrame());
        root.Add(CreateTargetFrame());
        root.Add(CreateActivityFrame());
        root.Add(CreateFailureFrame());
        return root;
    }

    public string RenderSnapshot()
    {
        var values = targets.Values.ToArray();
        var terminalCount = values.Count(static target => target.IsTerminal);
        var lines = new List<string>
        {
            $"Run ID: {plan.RunId}",
            $"Transport: {plan.Job.Transport}",
            $"Payload: {plan.Job.Payload.DisplayName}",
            $"Targets: {plan.Targets.Count}",
            $"Elapsed: {FormatDuration(GetElapsed())}",
            $"Progress: [{new string('#', GetProgressBlocks(terminalCount, values.Length)).PadRight(20, '-')}] {GetPercent(terminalCount, values.Length)}%",
            string.Empty,
            "Outcome",
            $"Active: {values.Count(static target => target.IsActive)} | Succeeded: {values.Count(static target => target.State == TargetExecutionState.Succeeded)} | Failed: {values.Count(static target => target.State == TargetExecutionState.Failed)} | Timed Out: {values.Count(static target => target.State == TargetExecutionState.TimedOut)} | Cancelled: {values.Count(static target => target.State == TargetExecutionState.Cancelled)}",
            string.Empty,
            "Targets"
        };

        foreach (var target in values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"  {TerminalGuiConsoleRenderer.FormatStatusSymbol(target.State)} {target.Name} | {FormatStateText(target.State)} | exit {target.ExitCode?.ToString() ?? "-"} | {FormatStatus(target)}");
        }

        if (recentEvents.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Recent Activity");
            lines.AddRange(recentEvents.Select(static item => $"  {item}"));
        }

        return TerminalGuiConsoleRenderer.BuildShellSnapshot("Dispatch Run Dashboard", lines);
    }

    private View CreateRunHeader()
    {
        var frame = new FrameView("Run")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 6
        };
        frame.Add(new Label($"Run ID: {plan.RunId}") { X = 1, Y = 0, Width = Dim.Percent(50) });
        frame.Add(new Label($"Transport: {plan.Job.Transport}") { X = Pos.Percent(50), Y = 0, Width = Dim.Fill(2) });
        frame.Add(new Label($"Payload: {plan.Job.Payload.DisplayName}") { X = 1, Y = 1, Width = Dim.Percent(50) });
        frame.Add(new Label($"Targets: {plan.Targets.Count}") { X = Pos.Percent(50), Y = 1, Width = Dim.Fill(2) });
        frame.Add(new Label($"Elapsed: {FormatDuration(GetElapsed())}") { X = 1, Y = 2, Width = Dim.Percent(50) });
        frame.Add(new Label($"Results: {plan.LocalResultsJsonPath}") { X = 1, Y = 3, Width = Dim.Fill(2) });
        return frame;
    }

    private View CreateProgressFrame()
    {
        var values = targets.Values.ToArray();
        var completed = values.Count(static target => target.IsTerminal);
        var frame = new FrameView("Progress")
        {
            X = 0,
            Y = 6,
            Width = Dim.Fill(),
            Height = 6
        };
        frame.Add(new ProgressBar
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Fraction = values.Length == 0 ? 0 : (float)completed / values.Length
        });
        frame.Add(new Label($"Active: {values.Count(static target => target.IsActive)}")
        {
            X = 1,
            Y = 2
        });
        frame.Add(new Label($"Succeeded: {values.Count(static target => target.State == TargetExecutionState.Succeeded)}")
        {
            X = 18,
            Y = 2
        });
        frame.Add(new Label($"Failed: {values.Count(static target => target.State == TargetExecutionState.Failed)}")
        {
            X = 38,
            Y = 2
        });
        frame.Add(new Label($"Timed Out: {values.Count(static target => target.State == TargetExecutionState.TimedOut)}")
        {
            X = 52,
            Y = 2
        });
        return frame;
    }

    private View CreateTargetFrame()
    {
        var frame = new FrameView("Targets")
        {
            X = 0,
            Y = 12,
            Width = Dim.Percent(65),
            Height = Dim.Fill(9)
        };
        var rows = targets.Values
            .OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static target => $"{TerminalGuiConsoleRenderer.FormatStatusSymbol(target.State)} {target.Name,-24} {FormatStateText(target.State),-20} exit {target.ExitCode?.ToString() ?? "-"}")
            .ToList();
        frame.Add(new ListView(rows)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2)
        });
        return frame;
    }

    private View CreateActivityFrame()
    {
        var frame = new FrameView("Recent Activity")
        {
            X = Pos.Percent(65),
            Y = 12,
            Width = Dim.Fill(),
            Height = Dim.Percent(45)
        };
        var rows = recentEvents.Count == 0
            ? new List<string> { "Waiting for execution progress..." }
            : recentEvents.ToList();
        frame.Add(new ListView(rows)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2)
        });
        return frame;
    }

    private View CreateFailureFrame()
    {
        var frame = new FrameView("Failures")
        {
            X = Pos.Percent(65),
            Y = Pos.Percent(45),
            Width = Dim.Fill(),
            Height = Dim.Fill(9)
        };
        var failures = targets.Values
            .Where(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled)
            .OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static target => $"{target.Name}: {target.FailureCategory} {target.Message}")
            .ToList();
        if (failures.Count == 0)
        {
            failures.Add("No target failures reported.");
        }

        frame.Add(new ListView(failures)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2)
        });
        return frame;
    }

    private void AddRecentEvent(DispatchExecutionProgress progress)
    {
        var message = progress.State == TargetExecutionState.Failed && !string.IsNullOrWhiteSpace(progress.Message)
            ? $" - {Trim(progress.Message, 80)}"
            : string.Empty;

        recentEvents.Enqueue(
            $"{progress.Timestamp:HH:mm:ss} {TerminalGuiConsoleRenderer.FormatStatusSymbol(progress.State)} {progress.Target} {FormatStateText(progress.State)}{message}");
        while (recentEvents.Count > RecentEventLimit)
        {
            recentEvents.Dequeue();
        }
    }

    private TimeSpan GetElapsed() =>
        completedResult is null
            ? DateTimeOffset.UtcNow - startedAt
            : completedResult.EndedAt - completedResult.StartedAt;

    private static string FormatStatus(TargetStatus target) =>
        target.State switch
        {
            TargetExecutionState.Succeeded => "Complete",
            TargetExecutionState.Failed => target.FailureCategory.ToString(),
            TargetExecutionState.TimedOut => "Timed out",
            TargetExecutionState.Cancelled => "Cancelled",
            TargetExecutionState.Pending => "Queued",
            TargetExecutionState.Resolving => "Resolving",
            TargetExecutionState.Probing => "Checking access",
            TargetExecutionState.PreparingScript => "Preparing script",
            TargetExecutionState.Executing => "Running script",
            TargetExecutionState.CollectingArtifacts => "Collecting artifacts",
            _ => target.State.ToString()
        };

    private static string FormatStateText(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.PreparingScript => "Preparing Script",
            TargetExecutionState.CollectingArtifacts => "Collecting Artifacts",
            _ => state.ToString()
        };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

    private static int GetPercent(int value, int total) =>
        total == 0 ? 0 : (int)Math.Round((double)value / total * 100);

    private static int GetProgressBlocks(int value, int total) =>
        total == 0 ? 0 : (int)Math.Round((double)value / total * 20);

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

        public bool IsTerminal => State is
            TargetExecutionState.Succeeded or
            TargetExecutionState.Failed or
            TargetExecutionState.TimedOut or
            TargetExecutionState.Cancelled;
    }
}
