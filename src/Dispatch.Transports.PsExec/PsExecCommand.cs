namespace Dispatch.Transports.PsExec;

public sealed record PsExecCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string RenderedCommand);
