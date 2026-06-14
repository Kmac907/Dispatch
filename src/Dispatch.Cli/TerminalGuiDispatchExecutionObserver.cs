using Dispatch.Core.Execution;

namespace Dispatch.Cli;

internal sealed class TerminalGuiDispatchExecutionObserver(
    TerminalGuiDispatchRunDashboard dashboard,
    Action refresh) : IDispatchExecutionObserver
{
    private readonly object gate = new();

    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            dashboard.Update(progress);
            refresh();
        }

        return Task.CompletedTask;
    }
}
