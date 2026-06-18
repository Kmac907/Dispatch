using System.Net;
using System.Net.Sockets;

namespace Dispatch.Transports.Psrp;

public sealed class PsrpDnsResolver : IPsrpDnsResolver
{
    public async Task<PsrpProbeResult> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            _ = await Dns.GetHostEntryAsync(target, cancellationToken).ConfigureAwait(false);
            return PsrpProbeResult.Success;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return PsrpProbeResult.Failed($"DNS resolution failed for '{target}': {exception.Message}");
        }
    }
}

public sealed class PsrpPortProbe : IPsrpPortProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public async Task<PsrpProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(target, port, timeout.Token).ConfigureAwait(false);
            return PsrpProbeResult.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PsrpProbeResult.Failed($"Timed out connecting to '{target}' on TCP port {port}.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return PsrpProbeResult.Failed($"Could not connect to '{target}' on TCP port {port}: {exception.Message}");
        }
    }
}
