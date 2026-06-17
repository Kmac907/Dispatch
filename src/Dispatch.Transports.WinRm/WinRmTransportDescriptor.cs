using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmTransportDescriptor : ITransportDescriptor
{
    public const string TransportName = "winrm";

    public TransportKind Kind => TransportKind.WinRm;

    public string Name => TransportName;

    public TransportCapabilities Capabilities { get; } = new(
        SupportsScriptExecution: true,
        SupportsCommandExecution: true,
        RequiresEndpointLocalScriptPath: true,
        SupportsNativeFileCopy: false,
        SupportsStreamedFileTransfer: true,
        SupportsPowerShellStreams: false,
        SupportsCurrentUser: true,
        SupportsExplicitCredential: false,
        SupportsRunAsSystem: false,
        SupportsCredentialDelegation: false);
}
