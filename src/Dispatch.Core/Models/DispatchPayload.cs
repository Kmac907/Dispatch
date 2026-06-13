using System.Text.Json.Serialization;

namespace Dispatch.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "payloadType")]
[JsonDerivedType(typeof(ScriptPayload), "script")]
[JsonDerivedType(typeof(CommandPayload), "command")]
public abstract record DispatchPayload
{
    [JsonIgnore]
    public abstract PayloadKind PayloadType { get; }

    public abstract string DisplayName { get; }
}

public sealed record ScriptPayload(string ScriptPath, IReadOnlyList<string> ScriptArguments) : DispatchPayload
{
    public override PayloadKind PayloadType => PayloadKind.Script;

    public override string DisplayName => Path.GetFileName(ScriptPath);
}

public sealed record CommandPayload(string CommandLine, string Shell, string? WorkingDirectory) : DispatchPayload
{
    public override PayloadKind PayloadType => PayloadKind.Command;

    public override string DisplayName => CommandLine;
}
