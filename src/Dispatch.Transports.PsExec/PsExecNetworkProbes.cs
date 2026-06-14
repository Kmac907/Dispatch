using System.Net;
using System.Net.Sockets;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecDnsResolver : IPsExecDnsResolver
{
    public async Task<PsExecProbeResult> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            _ = await Dns.GetHostEntryAsync(target, cancellationToken).ConfigureAwait(false);
            return PsExecProbeResult.Success;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return PsExecProbeResult.Failed($"DNS resolution failed for '{target}': {exception.Message}");
        }
    }
}

public sealed class PsExecPortProbe : IPsExecPortProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public async Task<PsExecProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(target, port, timeout.Token).ConfigureAwait(false);
            return PsExecProbeResult.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PsExecProbeResult.Failed($"Timed out connecting to '{target}' on TCP port {port}.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return PsExecProbeResult.Failed($"Could not connect to '{target}' on TCP port {port}: {exception.Message}");
        }
    }
}
