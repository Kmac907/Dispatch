using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Terminal.Gui;

namespace Dispatch.Cli;

internal sealed class TerminalGuiCompactDispatchProgress(
    ExecutionPlan plan,
    IDispatchExecutor executor,
    TextWriter statusWriter)
{
    public async Task<DispatchRunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var tracker = new CompactProgressTracker(plan);
        if (Console.IsErrorRedirected)
        {
            var redirectedResult = await executor.ExecuteAsync(plan, tracker, cancellationToken).ConfigureAwait(false);
            tracker.Complete(redirectedResult);
            statusWriter.Write(tracker.RenderSnapshot());
            return redirectedResult;
        }

        Application.Init();
        try
        {
            var top = Application.Top;
            top.RemoveAll();
            var root = tracker.BuildView();
            top.Add(root);
            Application.Refresh();

            var result = await executor.ExecuteAsync(
                    plan,
                    new TerminalGuiCompactObserver(tracker, static () => Application.Refresh()),
                    cancellationToken)
                .ConfigureAwait(false);
            tracker.Complete(result);
            root.RemoveAll();
            root.Add(tracker.BuildView().Subviews.ToArray());
            Application.Refresh();
            return result;
        }
        finally
        {
            Application.Shutdown();
            statusWriter.Write(tracker.RenderSnapshot());
        }
    }

    private sealed class TerminalGuiCompactObserver(
        CompactProgressTracker tracker,
        Action refresh) : IDispatchExecutionObserver
    {
        public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.Update(progress);
            refresh();
            return Task.CompletedTask;
        }
    }

    private sealed class CompactProgressTracker : IDispatchExecutionObserver
    {
        private readonly ExecutionPlan plan;
        private readonly Dictionary<string, TargetProgress> targets;
        private DispatchRunResult? result;

        public CompactProgressTracker(ExecutionPlan plan)
        {
            this.plan = plan;
            targets = plan.Targets.ToDictionary(
                static target => target.Target.Name,
                static target => new TargetProgress(target.Target.Name, TargetExecutionState.Pending));
        }

        public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Update(progress);
            return Task.CompletedTask;
        }

        public void Update(DispatchExecutionProgress progress)
        {
            targets[progress.Target] = new TargetProgress(progress.Target, progress.State);
        }

        public void Complete(DispatchRunResult runResult)
        {
            result = runResult;
            foreach (var target in runResult.Targets)
            {
                targets[target.Target] = new TargetProgress(target.Target, target.State, target.ExitCode, target.ResultPath);
            }
        }

        public Window BuildView()
        {
            var root = new Window("Compact Dispatch Progress")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(1)
            };

            var y = 1;
            foreach (var target in targets.Values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
            {
                root.Add(new Label($"{TerminalGuiConsoleRenderer.FormatStatusSymbol(target.State)} {target.Name,-24} {FormatState(target.State),-20}")
                {
                    X = 1,
                    Y = y,
                    Width = 52
                });
                root.Add(new ProgressBar
                {
                    X = 54,
                    Y = y,
                    Width = Dim.Fill(2),
                    Fraction = GetPercent(target.State) / 100f
                });
                y += 2;
            }

            return root;
        }

        public string RenderSnapshot()
        {
            var lines = new List<string>
            {
                $"Run ID: {plan.RunId}",
                $"Targets: {targets.Count}",
                string.Empty
            };

            foreach (var target in targets.Values.OrderBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"{TerminalGuiConsoleRenderer.FormatStatusSymbol(target.State)} {target.Name,-24} {FormatState(target.State),-20} [{new string('#', GetProgressBlocks(target.State)).PadRight(20, '-')}] {GetPercent(target.State)}%");
            }

            if (result is not null)
            {
                lines.Add(string.Empty);
                lines.Add($"Result file: {result.ResultPath}");
            }

            return TerminalGuiConsoleRenderer.BuildShellSnapshot("Compact Progress Complete", lines);
        }

        private static string FormatState(TargetExecutionState state) =>
            state switch
            {
                TargetExecutionState.Succeeded => "Complete",
                TargetExecutionState.Failed => "Failed",
                TargetExecutionState.TimedOut => "Timed out",
                TargetExecutionState.Cancelled => "Cancelled",
                TargetExecutionState.Pending => "Queued",
                TargetExecutionState.Resolving => "Resolving",
                TargetExecutionState.Probing => "Checking access",
                TargetExecutionState.PreparingScript => "Preparing script",
                TargetExecutionState.Executing => "Running script",
                TargetExecutionState.CollectingArtifacts => "Collecting artifacts",
                _ => state.ToString()
            };

        private static int GetPercent(TargetExecutionState state) =>
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

        private static int GetProgressBlocks(TargetExecutionState state) =>
            (int)Math.Round(GetPercent(state) / 5d);

        private sealed record TargetProgress(
            string Name,
            TargetExecutionState State,
            int? ExitCode = null,
            string? ResultPath = null);
    }
}
