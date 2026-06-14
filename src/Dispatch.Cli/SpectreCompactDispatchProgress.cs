using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Spectre.Console;

namespace Dispatch.Cli;

internal sealed class SpectreCompactDispatchProgress(
    ExecutionPlan plan,
    IDispatchExecutor executor,
    IAnsiConsole console)
{
    public async Task<DispatchRunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = await console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(Spinner.Known.Dots)
            ])
            .StartAsync(async context =>
            {
                var tasks = plan.Targets.ToDictionary(
                    static target => target.Target.Name,
                    target => context.AddTask(
                        $"[grey]○[/] {Markup.Escape(target.Target.Name)} [grey]Queued[/]",
                        maxValue: 100));
                var observer = new CompactProgressObserver(tasks);
                return await executor.ExecuteAsync(plan, observer, cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        RenderFinalSnapshot(result);
        return result;
    }

    private void RenderFinalSnapshot(DispatchRunResult result)
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(result.FailedCount == 0 && result.TimedOutCount == 0 && result.CancelledCount == 0
                ? Color.Green
                : Color.Red)
            .Expand();
        table.AddColumn("Status");
        table.AddColumn("Target");
        table.AddColumn("Phase");
        table.AddColumn("Progress");
        table.AddColumn("Exit");
        table.AddColumn("Result");

        foreach (var target in result.Targets)
        {
            table.AddRow(
                FormatStatusSymbol(target.State),
                Markup.Escape(target.Target),
                FormatState(target.State),
                target.State is TargetExecutionState.Succeeded
                    or TargetExecutionState.Failed
                    or TargetExecutionState.TimedOut
                    or TargetExecutionState.Cancelled
                    ? "[green]100%[/]"
                    : "[yellow]In progress[/]",
                target.ExitCode?.ToString() ?? "-",
                Markup.Escape(target.ResultPath));
        }

        console.Write(new Panel(table)
            .Header("[bold steelblue1] Compact Progress Complete [/]")
            .RoundedBorder()
            .BorderColor(Color.SteelBlue1)
            .Expand());
    }

    private sealed class CompactProgressObserver(IReadOnlyDictionary<string, ProgressTask> tasks) : IDispatchExecutionObserver
    {
        private readonly object gate = new();

        public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tasks.TryGetValue(progress.Target, out var task))
            {
                return Task.CompletedTask;
            }

            lock (gate)
            {
                task.Value = Math.Max(task.Value, GetPercent(progress.State));
                task.Description = $"{FormatStatusSymbol(progress.State)} {Markup.Escape(progress.Target)} {FormatState(progress.State)}";
                if (progress.State is TargetExecutionState.Succeeded
                    or TargetExecutionState.Failed
                    or TargetExecutionState.TimedOut
                    or TargetExecutionState.Cancelled)
                {
                    task.Value = 100;
                    task.StopTask();
                }
            }

            return Task.CompletedTask;
        }

        private static double GetPercent(TargetExecutionState state) =>
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

        private static string FormatState(TargetExecutionState state) =>
            state switch
            {
                TargetExecutionState.Succeeded => "[green]Complete[/]",
                TargetExecutionState.Failed => "[red]Failed[/]",
                TargetExecutionState.TimedOut => "[yellow]Timed out[/]",
                TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
                TargetExecutionState.Pending => "[grey]Queued[/]",
                TargetExecutionState.Resolving => "[steelblue1]Resolving[/]",
                TargetExecutionState.Probing => "[steelblue1]Checking access[/]",
                TargetExecutionState.PreparingScript => "[steelblue1]Preparing script[/]",
                TargetExecutionState.Executing => "[steelblue1]Running script[/]",
                TargetExecutionState.CollectingArtifacts => "[steelblue1]Collecting artifacts[/]",
                _ => Markup.Escape(state.ToString())
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
    }

    private static string FormatState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]Complete[/]",
            TargetExecutionState.Failed => "[red]Failed[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            TargetExecutionState.Pending => "[grey]Queued[/]",
            TargetExecutionState.Resolving => "[steelblue1]Resolving[/]",
            TargetExecutionState.Probing => "[steelblue1]Checking access[/]",
            TargetExecutionState.PreparingScript => "[steelblue1]Preparing script[/]",
            TargetExecutionState.Executing => "[steelblue1]Running script[/]",
            TargetExecutionState.CollectingArtifacts => "[steelblue1]Collecting artifacts[/]",
            _ => Markup.Escape(state.ToString())
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
}
