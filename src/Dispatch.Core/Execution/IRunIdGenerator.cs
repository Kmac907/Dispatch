namespace Dispatch.Core.Execution;

public interface IRunIdGenerator
{
    string CreateRunId();
}

internal sealed class DispatchRunIdGenerator : IRunIdGenerator
{
    public string CreateRunId() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..31];
}
