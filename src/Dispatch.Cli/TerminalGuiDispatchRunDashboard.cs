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
        var root = new Window("Dispatch Run Monitor")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        root.Add(CreateRunHeader());
        root.Add(CreateOutcomeFrame());
        root.Add(CreatePhaseFrame());
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
            "Outcome Chart",
            $"Active: {values.Count(static target => target.IsActive)} | Succeeded: {values.Count(static target => target.State == TargetExecutionState.Succeeded)} | Failed: {values.Count(static target => target.State == TargetExecutionState.Failed)} | Timed Out: {values.Count(static target => target.State == TargetExecutionState.TimedOut)} | Cancelled: {values.Count(static target => target.State == TargetExecutionState.Cancelled)}",
            $"Queued   [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.Pending), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.Pending)}",
            $"Active   [{BuildSnapshotBar(values.Count(static target => target.IsActive), values.Length)}] {values.Count(static target => target.IsActive)}",
            $"Complete [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.Succeeded), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.Succeeded)}",
            $"Failed   [{BuildSnapshotBar(values.Count(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled), values.Length)}] {values.Count(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled)}",
            string.Empty,
            "Phase Distribution",
            $"Probe   [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.Probing), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.Probing)}",
            $"Prepare [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.PreparingScript), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.PreparingScript)}",
            $"Execute [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.Executing), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.Executing)}",
            $"Collect [{BuildSnapshotBar(values.Count(static target => target.State == TargetExecutionState.CollectingArtifacts), values.Length)}] {values.Count(static target => target.State == TargetExecutionState.CollectingArtifacts)}",
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
            Height = 5
        };
        frame.Add(new Label($"Run {plan.RunId}") { X = 1, Y = 0, Width = Dim.Percent(35) });
        frame.Add(new Label($"Transport {plan.Job.Transport}") { X = Pos.Percent(35), Y = 0, Width = Dim.Percent(25) });
        frame.Add(new Label($"Targets {plan.Targets.Count}") { X = Pos.Percent(60), Y = 0, Width = Dim.Percent(18) });
        frame.Add(new Label($"Elapsed {FormatDuration(GetElapsed())}") { X = Pos.Percent(78), Y = 0, Width = Dim.Fill(2) });
        frame.Add(new Label($"Payload {plan.Job.Payload.DisplayName}") { X = 1, Y = 1, Width = Dim.Fill(2) });
        frame.Add(new Label($"Results {Trim(plan.LocalResultsJsonPath, 120)}") { X = 1, Y = 2, Width = Dim.Fill(2) });
        return frame;
    }

    private View CreateOutcomeFrame()
    {
        var values = targets.Values.ToArray();
        var completed = values.Count(static target => target.IsTerminal);
        var failed = values.Count(static target => target.State is TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled);
        var frame = new FrameView("Progress And Outcome")
        {
            X = 0,
            Y = 5,
            Width = Dim.Percent(58),
            Height = 10
        };
        frame.Add(new ProgressBar
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Fraction = GetFraction(completed, values.Length)
        });
        frame.Add(new Label($"{GetPercent(completed, values.Length)}% terminal") { X = 1, Y = 1, Width = Dim.Fill(2) });
        AddMetric(frame, "Queued", values.Count(static target => target.State == TargetExecutionState.Pending), values.Length, 3);
        AddMetric(frame, "Active", values.Count(static target => target.IsActive), values.Length, 4);
        AddMetric(frame, "Succeeded", values.Count(static target => target.State == TargetExecutionState.Succeeded), values.Length, 5);
        AddMetric(frame, "Failed", failed, values.Length, 6);
        return frame;
    }

    private View CreatePhaseFrame()
    {
        var values = targets.Values.ToArray();
        var frame = new FrameView("Phase Distribution")
        {
            X = Pos.Percent(58),
            Y = 5,
            Width = Dim.Fill(),
            Height = 10
        };

        AddMetric(frame, "Probe", values.Count(static target => target.State == TargetExecutionState.Probing), values.Length, 0);
        AddMetric(frame, "Prepare", values.Count(static target => target.State == TargetExecutionState.PreparingScript), values.Length, 1);
        AddMetric(frame, "Execute", values.Count(static target => target.State == TargetExecutionState.Executing), values.Length, 2);
        AddMetric(frame, "Collect", values.Count(static target => target.State == TargetExecutionState.CollectingArtifacts), values.Length, 3);
        AddMetric(frame, "Done", values.Count(static target => target.IsTerminal), values.Length, 4);
        return frame;
    }

    private View CreateTargetFrame()
    {
        var frame = new FrameView("Targets")
        {
            X = 0,
            Y = 15,
            Width = Dim.Percent(60),
            Height = Dim.Fill(8)
        };
        var rows = targets.Values
            .OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static target => $"{TerminalGuiConsoleRenderer.FormatStatusSymbol(target.State)} {target.Name,-22} {FormatStateText(target.State),-20} {GetPercentForState(target.State),3}% exit {target.ExitCode?.ToString() ?? "-"}")
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
            X = Pos.Percent(60),
            Y = 15,
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
            X = Pos.Percent(60),
            Y = Pos.Percent(45),
            Width = Dim.Fill(),
            Height = Dim.Fill(8)
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

    private static float GetFraction(int value, int total) =>
        total == 0 ? 0 : Math.Clamp((float)value / total, 0, 1);

    private static void AddMetric(View parent, string label, int value, int total, int y)
    {
        parent.Add(new Label($"{label,-9} {value,3}") { X = 1, Y = y, Width = 16 });
        parent.Add(new ProgressBar
        {
            X = 18,
            Y = y,
            Width = Dim.Fill(2),
            Fraction = GetFraction(value, total)
        });
    }

    private static string BuildSnapshotBar(int value, int total) =>
        new string('#', GetProgressBlocks(value, total)).PadRight(20, '-');

    private static int GetPercentForState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Pending => 0,
            TargetExecutionState.Resolving => 10,
            TargetExecutionState.Probing => 25,
            TargetExecutionState.PreparingScript => 45,
            TargetExecutionState.Executing => 70,
            TargetExecutionState.CollectingArtifacts => 90,
            TargetExecutionState.Succeeded => 100,
            TargetExecutionState.Failed => 100,
            TargetExecutionState.TimedOut => 100,
            TargetExecutionState.Cancelled => 100,
            _ => 0
        };

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
