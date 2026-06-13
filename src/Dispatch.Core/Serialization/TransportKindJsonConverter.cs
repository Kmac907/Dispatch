using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Models;

namespace Dispatch.Core.Serialization;

public sealed class TransportKindJsonConverter : JsonConverter<TransportKind>
{
    public override TransportKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        return value?.ToLowerInvariant() switch
        {
            "psexec" => TransportKind.PsExec,
            "psrp" => TransportKind.Psrp,
            "winrm" => TransportKind.WinRm,
            _ => throw new JsonException($"Unknown transport kind '{value}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, TransportKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            TransportKind.PsExec => "psexec",
            TransportKind.Psrp => "psrp",
            TransportKind.WinRm => "winrm",
            _ => throw new JsonException($"Unknown transport kind '{value}'.")
        });
    }
}
