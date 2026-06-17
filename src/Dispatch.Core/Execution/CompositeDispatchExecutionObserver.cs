namespace Dispatch.Core.Execution;

internal sealed class CompositeDispatchExecutionObserver(params IDispatchExecutionObserver[] observers)
    : IDispatchExecutionObserver
{
    public async Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            await observer.OnProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        }
    }
}
