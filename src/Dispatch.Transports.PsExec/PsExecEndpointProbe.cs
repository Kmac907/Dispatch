using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecEndpointProbe(
    IPsExecDnsResolver dnsResolver,
    IPsExecPortProbe portProbe,
    IPsExecAdminShareProbe adminShareProbe) : ITransportEndpointProbe
{
    private const int SmbPort = 445;

    public TransportKind Kind => TransportKind.PsExec;

    public async Task<TransportEndpointProbeResult> ProbeAsync(
        TransportEndpointProbeRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>
        {
            ["probe"] = "psexec",
            ["target"] = request.Target.Target.Name
        };

        var dns = await dnsResolver.ResolveAsync(request.Target.Target.Name, cancellationToken).ConfigureAwait(false);
        if (!dns.Succeeded)
        {
            metadata["stage"] = "dns";
            return Failed(startedAt, FailureCategory.ProbeFailed, dns.FailureMessage, metadata);
        }

        metadata["dns"] = "ok";
        var smb = await portProbe.CanConnectAsync(request.Target.Target.Name, SmbPort, cancellationToken).ConfigureAwait(false);
        if (!smb.Succeeded)
        {
            metadata["stage"] = "smb";
            metadata["port"] = SmbPort.ToString();
            return Failed(startedAt, FailureCategory.TransportUnavailable, smb.FailureMessage, metadata);
        }

        metadata["smb"] = "ok";
        if (string.IsNullOrWhiteSpace(request.Target.PlannedRemoteScriptPath))
        {
            metadata["stage"] = "admin-share";
            return Failed(
                startedAt,
                FailureCategory.PayloadPreparationFailed,
                $"No planned remote script path exists for target '{request.Target.Target.Name}'.",
                metadata);
        }

        var adminSharePath = AdminSharePath.FromRemoteWindowsPath(request.Target.Target.Name, request.Target.PlannedRemoteScriptPath);
        if (!adminSharePath.IsValid)
        {
            metadata["stage"] = "admin-share";
            return Failed(startedAt, FailureCategory.PayloadPreparationFailed, adminSharePath.Error!.Message, metadata);
        }

        var adminShareRoot = GetAdminShareRoot(adminSharePath.Path!);
        metadata["adminShareRoot"] = adminShareRoot;
        var adminShare = await adminShareProbe.ProbeDirectoryAsync(adminShareRoot, cancellationToken).ConfigureAwait(false);
        if (!adminShare.Succeeded)
        {
            metadata["stage"] = "admin-share";
            var category = adminShare.FailureKind == PsExecAdminShareFailureKind.Authorization
                ? FailureCategory.AuthorizationFailed
                : FailureCategory.TransportUnavailable;
            return Failed(startedAt, category, adminShare.FailureMessage, metadata);
        }

        metadata["adminShare"] = "ok";
        return new TransportEndpointProbeResult(
            Succeeded: true,
            StartedAt: startedAt,
            EndedAt: DateTimeOffset.UtcNow,
            Metadata: metadata);
    }

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

    private static string GetAdminShareRoot(string adminSharePath)
    {
        var parts = adminSharePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $@"\\{parts[0]}\{parts[1]}"
            : adminSharePath;
    }
}
