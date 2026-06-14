namespace Dispatch.Core.Targeting;

public sealed record TargetResolutionInput(
    IReadOnlyList<string> ComputerNameValues,
    string? TargetFile);
