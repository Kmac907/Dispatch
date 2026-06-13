namespace Dispatch.Core.Transports;

public sealed record TransportCapabilities(
    bool SupportsScriptExecution,
    bool SupportsCommandExecution,
    bool RequiresEndpointLocalScriptPath,
    bool SupportsNativeFileCopy,
    bool SupportsStreamedFileTransfer,
    bool SupportsPowerShellStreams,
    bool SupportsCurrentUser,
    bool SupportsExplicitCredential,
    bool SupportsRunAsSystem,
    bool SupportsCredentialDelegation);
