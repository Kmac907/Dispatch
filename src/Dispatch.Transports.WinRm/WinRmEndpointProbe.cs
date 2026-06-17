using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmEndpointProbe(
    IWinRmDnsResolver dnsResolver,
    IWinRmPortProbe portProbe) : ITransportEndpointProbe
{
    private const int HttpPort = 5985;
    private const int HttpsPort = 5986;

    public TransportKind Kind => TransportKind.WinRm;

    public async Task<TransportEndpointProbeResult> ProbeAsync(
        TransportEndpointProbeRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>
        {
            ["probe"] = "winrm",
            ["target"] = request.Target.Target.Name
        };

        var dns = await dnsResolver.ResolveAsync(request.Target.Target.Name, cancellationToken).ConfigureAwait(false);
        if (!dns.Succeeded)
        {
            metadata["stage"] = "dns";
            return Failed(startedAt, FailureCategory.ProbeFailed, dns.FailureMessage, metadata);
        }

        metadata["dns"] = "ok";

        var http = await portProbe.CanConnectAsync(request.Target.Target.Name, HttpPort, cancellationToken).ConfigureAwait(false);
        if (http.Succeeded)
        {
            metadata["port"] = HttpPort.ToString();
            metadata["scheme"] = "http";
            return Succeeded(startedAt, metadata);
        }

        var https = await portProbe.CanConnectAsync(request.Target.Target.Name, HttpsPort, cancellationToken).ConfigureAwait(false);
        if (https.Succeeded)
        {
            metadata["port"] = HttpsPort.ToString();
            metadata["scheme"] = "https";
            return Succeeded(startedAt, metadata);
        }

        metadata["stage"] = "port";
        metadata["attemptedPorts"] = $"{HttpPort},{HttpsPort}";
        return Failed(
            startedAt,
            FailureCategory.TransportUnavailable,
            $"WinRM ports are unreachable for '{request.Target.Target.Name}'. HTTP: {http.FailureMessage} HTTPS: {https.FailureMessage}",
            metadata);
    }

    private static TransportEndpointProbeResult Succeeded(
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            Succeeded: true,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            Metadata: metadata);

    private static TransportEndpointProbeResult Failed(
        DateTimeOffset startedAt,
        FailureCategory failureCategory,
        string? failureMessage,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            Succeeded: false,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            FailureCategory: failureCategory,
            FailureMessage: failureMessage,
            Metadata: metadata);
}
