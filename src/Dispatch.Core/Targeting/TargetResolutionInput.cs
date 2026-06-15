namespace Dispatch.Core.Targeting;

public sealed record TargetResolutionInput(
    IReadOnlyList<string> ComputerNameValues,
    string? TargetFile,
    IReadOnlyList<string>? TargetSelectors = null,
    string? InventoryPath = null,
    IReadOnlyList<string>? ExcludeSelectors = null);
