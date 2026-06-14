namespace Dispatch.Core.Execution;

public interface IDispatchExecutionObserver
{
    Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken);
}

public sealed class NullDispatchExecutionObserver : IDispatchExecutionObserver
{
    public static NullDispatchExecutionObserver Instance { get; } = new();

    private NullDispatchExecutionObserver()
    {
    }

    public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
