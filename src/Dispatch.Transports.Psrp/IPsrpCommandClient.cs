namespace Dispatch.Transports.Psrp;

public interface IPsrpCommandClient
{
    Task<PsrpCommandResult> ExecuteAsync(PsrpCommandRequest request, CancellationToken cancellationToken);
}
