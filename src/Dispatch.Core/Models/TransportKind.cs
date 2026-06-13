using Dispatch.Core.Serialization;
using System.Text.Json.Serialization;

namespace Dispatch.Core.Models;

[JsonConverter(typeof(TransportKindJsonConverter))]
public enum TransportKind
{
    PsExec,
    Psrp,
    WinRm
}
