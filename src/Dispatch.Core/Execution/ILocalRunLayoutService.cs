using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public interface ILocalRunLayoutService
{
    LocalRunLayoutResult Prepare(
        string localRoot,
        string runId,
        IReadOnlyList<TargetSpec> targets,
        bool createDirectories);
}
