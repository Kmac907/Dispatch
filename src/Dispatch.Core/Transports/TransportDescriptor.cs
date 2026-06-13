using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public interface ITransportDescriptor
{
    TransportKind Kind { get; }

    string Name { get; }

    TransportCapabilities Capabilities { get; }
}
