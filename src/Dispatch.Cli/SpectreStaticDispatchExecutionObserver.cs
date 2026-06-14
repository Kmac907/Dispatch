using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Spectre.Console;

namespace Dispatch.Cli;

internal sealed class SpectreStaticDispatchExecutionObserver(IAnsiConsole console) : IDispatchExecutionObserver
{
    private readonly object gate = new();

    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            var table = new Table()
                .RoundedBorder()
                .BorderColor(GetBorderColor(progress.State))
                .Expand();
            table.AddColumn("Target");
            table.AddColumn("Phase");
            table.AddColumn("Time");
            table.AddColumn("Detail");
            table.AddRow(
                Markup.Escape(progress.Target),
                FormatState(progress.State),
                Markup.Escape(progress.Timestamp.ToLocalTime().ToString("HH:mm:ss")),
                Markup.Escape(FormatDetail(progress)));

            console.Write(new Panel(table)
                .Header("[bold steelblue1] Target Progress [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(GetBorderColor(progress.State)));
        }

        return Task.CompletedTask;
    }

    private static string FormatDetail(DispatchExecutionProgress progress)
    {
        if (progress.FailureCategory != FailureCategory.None)
        {
            return string.IsNullOrWhiteSpace(progress.Message)
                ? progress.FailureCategory.ToString()
                : $"{progress.FailureCategory}: {progress.Message}";
        }

        return progress.Message ?? "Running";
    }

    private static string FormatState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => "[green]Succeeded[/]",
            TargetExecutionState.Failed => "[red]Failed[/]",
            TargetExecutionState.TimedOut => "[yellow]Timed Out[/]",
            TargetExecutionState.Cancelled => "[grey]Cancelled[/]",
            TargetExecutionState.PreparingScript => "[steelblue1]Preparing Script[/]",
            TargetExecutionState.CollectingArtifacts => "[steelblue1]Collecting Artifacts[/]",
            _ => $"[steelblue1]{Markup.Escape(state.ToString())}[/]"
        };

    private static Color GetBorderColor(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.Succeeded => Color.Green,
            TargetExecutionState.Failed => Color.Red,
            TargetExecutionState.TimedOut => Color.Yellow,
            TargetExecutionState.Cancelled => Color.Grey,
            _ => Color.SteelBlue1
        };
}
