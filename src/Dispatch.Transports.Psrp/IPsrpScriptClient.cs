using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public interface IPsrpScriptClient
{
    Task<PsrpCommandResult> ExecuteAsync(PsrpScriptRequest request, CancellationToken cancellationToken);
}
