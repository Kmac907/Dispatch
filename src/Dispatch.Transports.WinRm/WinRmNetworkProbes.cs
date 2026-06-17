using System.Net;
using System.Net.Sockets;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmDnsResolver : IWinRmDnsResolver
{
    public async Task<WinRmProbeResult> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            _ = await Dns.GetHostEntryAsync(target, cancellationToken).ConfigureAwait(false);
            return WinRmProbeResult.Success;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return WinRmProbeResult.Failed($"DNS resolution failed for '{target}': {exception.Message}");
        }
    }
}

public sealed class WinRmPortProbe : IWinRmPortProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public async Task<WinRmProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(target, port, timeout.Token).ConfigureAwait(false);
            return WinRmProbeResult.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WinRmProbeResult.Failed($"Timed out connecting to '{target}' on TCP port {port}.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return WinRmProbeResult.Failed($"Could not connect to '{target}' on TCP port {port}: {exception.Message}");
        }
    }
}
