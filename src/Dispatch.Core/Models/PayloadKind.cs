using System.Text.Json.Serialization;

namespace Dispatch.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PayloadKind>))]
public enum PayloadKind
{
    Script,
    Command
}
