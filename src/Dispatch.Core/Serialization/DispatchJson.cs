using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Core.Serialization;

namespace Dispatch.Core;

public static class DispatchJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        options.Converters.Add(new TransportKindJsonConverter());
        options.Converters.Add(new DispatchPayloadJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
