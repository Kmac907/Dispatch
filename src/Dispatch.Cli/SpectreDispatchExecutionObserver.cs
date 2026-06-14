using Dispatch.Core.Execution;
using Spectre.Console;

namespace Dispatch.Cli;

internal sealed class SpectreDispatchExecutionObserver(
    SpectreDispatchRunDashboard dashboard,
    LiveDisplayContext context,
    object gate) : IDispatchExecutionObserver
{
    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            dashboard.Update(progress);
            context.UpdateTarget(dashboard.Render());
            context.Refresh();
        }

        return Task.CompletedTask;
    }
}
