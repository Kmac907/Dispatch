using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dispatch.Core.Redaction;

public static partial class DispatchRedactor
{
    public const string RedactedValue = "[redacted]";

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = SharedAccessSignatureRegex().Replace(value, $"$1{RedactedValue}");
        redacted = SasSignatureRegex().Replace(redacted, $"$1{RedactedValue}");
        redacted = NamedSecretRegex().Replace(redacted, $"$1{RedactedValue}");
        return redacted;
    }

    public static string RedactJson(string json, bool writeIndented = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        var node = JsonNode.Parse(json);
        return RedactJsonNode(node)?.ToJsonString(new JsonSerializerOptions(DispatchJson.Options) { WriteIndented = writeIndented })
            ?? json;
    }

    public static JsonNode? RedactJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                var redactedObject = new JsonObject();
                foreach (var property in jsonObject)
                {
                    redactedObject[property.Key] = RedactJsonNode(property.Value);
                }

                return redactedObject;

            case JsonArray jsonArray:
                var redactedArray = new JsonArray();
                foreach (var item in jsonArray)
                {
                    redactedArray.Add(RedactJsonNode(item));
                }

                return redactedArray;

            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                return JsonValue.Create(Redact(text));

            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex(@"(?i)(SharedAccessSignature=)[^;\s""']+")]
    private static partial Regex SharedAccessSignatureRegex();

    [GeneratedRegex(@"(?i)(sig=)[^&\s""']+")]
    private static partial Regex SasSignatureRegex();

    [GeneratedRegex(@"(?i)((?:password|passwd|pwd|secret|token|sastoken)\s*[=:]\s*)[^\s,;""']+")]
    private static partial Regex NamedSecretRegex();
}
