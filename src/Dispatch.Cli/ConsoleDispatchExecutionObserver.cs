using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Cli;

internal sealed class ConsoleDispatchExecutionObserver(TextWriter writer) : IDispatchExecutionObserver
{
    private readonly object gate = new();

    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var line = progress.State == TargetExecutionState.Failed
            ? $"{progress.Target}: {FormatState(progress.State)} ({progress.FailureCategory}) {progress.Message}".TrimEnd()
            : $"{progress.Target}: {FormatState(progress.State)}";

        lock (gate)
        {
            writer.WriteLine(line);
        }

        return Task.CompletedTask;
    }

    private static string FormatState(TargetExecutionState state) =>
        state switch
        {
            TargetExecutionState.PreparingScript => "preparing script",
            TargetExecutionState.CollectingArtifacts => "collecting artifacts",
            _ => state.ToString().ToLowerInvariant()
        };
}
