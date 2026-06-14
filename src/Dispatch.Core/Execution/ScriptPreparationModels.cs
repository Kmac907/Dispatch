using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public sealed record TargetScriptManifest(
    TargetSpec Target,
    string RemoteScriptPath,
    string? AdminShareScriptPath);

public sealed record ScriptExecutionManifest(
    string SourceScriptPath,
    IReadOnlyList<string> ScriptArguments,
    string RemoteScriptDirectory,
    IReadOnlyList<TargetScriptManifest> Targets);

public sealed record TargetScriptPreparationResult(
    TargetSpec Target,
    string RemoteScriptPath,
    string? AdminShareScriptPath,
    bool Succeeded,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null);

public sealed record ScriptPreparationResult(
    ScriptExecutionManifest? Manifest,
    IReadOnlyList<TargetScriptPreparationResult> Targets)
{
    public bool Succeeded => Targets.Count > 0 && Targets.All(static target => target.Succeeded);
}
