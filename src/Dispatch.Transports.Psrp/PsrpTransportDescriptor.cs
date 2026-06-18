using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.Psrp;

public sealed class PsrpTransportDescriptor : ITransportDescriptor
{
    public const string TransportName = "psrp";

    public TransportKind Kind => TransportKind.Psrp;

    public string Name => TransportName;

    public TransportCapabilities Capabilities { get; } = new(
        SupportsScriptExecution: true,
        SupportsCommandExecution: true,
        RequiresEndpointLocalScriptPath: false,
        SupportsNativeFileCopy: false,
        SupportsStreamedFileTransfer: false,
        SupportsPowerShellStreams: false,
        SupportsCurrentUser: true,
        SupportsExplicitCredential: false,
        SupportsRunAsSystem: false,
        SupportsCredentialDelegation: false);
}
