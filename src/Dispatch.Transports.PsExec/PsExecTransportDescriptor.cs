using Dispatch.Core.Models;
using Dispatch.Core.Transports;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecTransportDescriptor : ITransportDescriptor
{
    public const string TransportName = "psexec";

    public TransportKind Kind => TransportKind.PsExec;

    public string Name => TransportName;

    public TransportCapabilities Capabilities { get; } = new(
        SupportsScriptExecution: true,
        SupportsCommandExecution: false,
        RequiresEndpointLocalScriptPath: true,
        SupportsNativeFileCopy: true,
        SupportsStreamedFileTransfer: false,
        SupportsPowerShellStreams: false,
        SupportsCurrentUser: true,
        SupportsExplicitCredential: false,
        SupportsRunAsSystem: true,
        SupportsCredentialDelegation: false);
}
