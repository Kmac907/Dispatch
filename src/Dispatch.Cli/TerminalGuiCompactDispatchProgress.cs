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
            TerminalGuiTheme.Apply();
            var top = Application.Top;
            top.RemoveAll();
            var root = tracker.BuildView();
            top.Add(root);
            Application.Refresh();

            void Refresh()
            {
                root.RemoveAll();
                root.Add(tracker.BuildView().Subviews.ToArray());
                Application.Refresh();
            }

            DispatchRunResult? runResult = null;
            Exception? runException = null;
            var runTask = Task.Run(
                async () =>
                {
                    try
                    {
                        runResult = await executor.ExecuteAsync(
                                plan,
                                new TerminalGuiCompactObserver(tracker, () => InvokeOnTerminalGuiLoop(Refresh)),
                                cancellationToken)
                            .ConfigureAwait(false);
                        tracker.Complete(runResult);
                        InvokeOnTerminalGuiLoop(Refresh);
                    }
                    catch (Exception exception)
                    {
                        runException = exception;
                    }
                    finally
                    {
                        InvokeOnTerminalGuiLoop(static () => Application.RequestStop());
                    }
                },
                CancellationToken.None);

            Application.Run();
            await runTask.ConfigureAwait(false);
            if (runException is not null)
            {
                throw runException;
            }

            return runResult
                ?? throw new InvalidOperationException("Dispatch execution ended without a run result.");
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

    private static void InvokeOnTerminalGuiLoop(Action action)
    {
        var mainLoop = Application.MainLoop;
        if (mainLoop is null)
        {
            action();
            return;
        }

        mainLoop.Invoke(action);
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

            var values = targets.Values.ToArray();
            var terminal = values.Count(static target => IsTerminal(target.State));
            root.Add(new Label($"Run {plan.RunId}") { X = 1, Y = 0, Width = Dim.Percent(45) });
            root.Add(new Label($"Targets {values.Length}") { X = Pos.Percent(45), Y = 0, Width = 14 });
            root.Add(new Label($"Complete {terminal}/{values.Length}") { X = Pos.Percent(62), Y = 0, Width = Dim.Fill(2) });
            root.Add(new ProgressBar
            {
                X = 1,
                Y = 2,
                Width = Dim.Fill(2),
                Fraction = values.Length == 0 ? 0 : (float)terminal / values.Length
            });

            var y = 4;
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

        private static bool IsTerminal(TargetExecutionState state) =>
            state is TargetExecutionState.Succeeded or TargetExecutionState.Failed or TargetExecutionState.TimedOut or TargetExecutionState.Cancelled;

        private sealed record TargetProgress(
            string Name,
            TargetExecutionState State,
            int? ExitCode = null,
            string? ResultPath = null);
    }
}
