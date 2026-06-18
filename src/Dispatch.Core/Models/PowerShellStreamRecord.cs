namespace Dispatch.Core.Models;

public sealed record PowerShellStreamRecord(
    PowerShellStreamKind Stream,
    string Message);
