namespace Dispatch.Transports.PsExec;

public interface IPsExecProcessRunner
{
    Task<PsExecProcessResult> RunAsync(PsExecCommand command, CancellationToken cancellationToken);
}
