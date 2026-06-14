using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecScriptExecutor(
    PsExecCommandBuilder commandBuilder,
    IPsExecProcessRunner processRunner) : ITransportScriptExecutor
{
    public TransportKind Kind => TransportKind.PsExec;

    public async Task<TransportScriptExecutionResult> ExecuteScriptAsync(
        TransportScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var command = commandBuilder.Build(request);
        var processResult = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        var expectedExitCodes = request.Plan.Job.ExpectedExitCodes;
        var metadata = new Dictionary<string, string>
        {
            ["psexecCommand"] = command.RenderedCommand,
            ["psexecPath"] = command.Executable,
            ["runAsSystem"] = request.Plan.Job.ExecutionContext.RunAsSystem.ToString()
        };

        if (processResult.FailureCategory != FailureCategory.None)
        {
            return new TransportScriptExecutionResult(
                ExitCode: processResult.ExitCode,
                Stdout: processResult.Stdout,
                Stderr: processResult.Stderr,
                StartedAt: processResult.StartedAt,
                EndedAt: processResult.EndedAt,
                FailureCategory: processResult.FailureCategory,
                FailureMessage: processResult.FailureMessage,
                Metadata: metadata);
        }

        var succeeded = processResult.ExitCode is not null && expectedExitCodes.Contains(processResult.ExitCode.Value);
        return new TransportScriptExecutionResult(
            ExitCode: processResult.ExitCode,
            Stdout: processResult.Stdout,
            Stderr: processResult.Stderr,
            StartedAt: processResult.StartedAt,
            EndedAt: processResult.EndedAt,
            FailureCategory: succeeded ? FailureCategory.None : FailureCategory.UnexpectedExitCode,
            FailureMessage: succeeded
                ? null
                : $"PsExec exited with code {processResult.ExitCode}; expected {string.Join(", ", expectedExitCodes)}.",
            Metadata: metadata);
    }
}
