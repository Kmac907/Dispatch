namespace Dispatch.Transports.Psrp;

public sealed record PsrpCommandRequest(
    string Target,
    string Executable,
    string Arguments,
    string? WorkingDirectory,
    TimeSpan? ExecutionTimeout);
