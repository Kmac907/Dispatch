namespace Dispatch.Transports.Psrp;

public sealed record PsrpScriptRequest(
    string Target,
    string ScriptPath,
    IReadOnlyList<string> ScriptArguments,
    TimeSpan? ExecutionTimeout,
    string RemoteScriptPath);
