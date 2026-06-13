using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Models;

namespace Dispatch.Core.Serialization;

public sealed class DispatchPayloadJsonConverter : JsonConverter<DispatchPayload>
{
    public override DispatchPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty("payloadType", out var payloadTypeProperty))
        {
            throw new JsonException("Payload is missing required property 'payloadType'.");
        }

        var payloadType = payloadTypeProperty.GetString();

        return payloadType?.ToLowerInvariant() switch
        {
            "script" => new ScriptPayload(
                ReadRequiredString(root, "scriptPath"),
                ReadStringArray(root, "scriptArguments")),
            "command" => new CommandPayload(
                ReadRequiredString(root, "commandLine"),
                ReadRequiredString(root, "shell"),
                ReadOptionalString(root, "workingDirectory")),
            _ => throw new JsonException($"Unknown payload type '{payloadType}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, DispatchPayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case ScriptPayload script:
                writer.WriteString("payloadType", "script");
                writer.WriteString("scriptPath", script.ScriptPath);
                writer.WriteStartArray("scriptArguments");
                foreach (var argument in script.ScriptArguments)
                {
                    writer.WriteStringValue(argument);
                }

                writer.WriteEndArray();
                writer.WriteString("displayName", script.DisplayName);
                break;

            case CommandPayload command:
                writer.WriteString("payloadType", "command");
                writer.WriteString("commandLine", command.CommandLine);
                writer.WriteString("shell", command.Shell);
                writer.WriteString("workingDirectory", command.WorkingDirectory);
                writer.WriteString("displayName", command.DisplayName);
                break;

            default:
                throw new JsonException($"Unsupported payload type '{value.GetType().Name}'.");
        }

        writer.WriteEndObject();
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Payload is missing required string property '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw new JsonException($"Payload property '{propertyName}' must be a string or null.");
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Payload property '{propertyName}' must be an array.");
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Payload property '{propertyName}' must contain only strings.");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }
}
