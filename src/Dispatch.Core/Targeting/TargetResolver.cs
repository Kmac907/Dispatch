using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Targeting;

public static class TargetResolver
{
    public static TargetResolutionResult Resolve(TargetResolutionInput input)
    {
        var targets = new List<TargetSpec>();
        var errors = new List<DispatchValidationError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in input.ComputerNameValues ?? Array.Empty<string>())
        {
            AddComputerNameTargets(value, targets, seen);
        }

        if (!string.IsNullOrWhiteSpace(input.TargetFile))
        {
            AddTargetFileTargets(input.TargetFile, targets, seen, errors);
        }

        if (targets.Count == 0 && errors.Count == 0)
        {
            errors.Add(new("TargetsRequired", "At least one target is required from --computer-name or --target-file."));
        }

        return new TargetResolutionResult(targets, errors);
    }

    private static void AddComputerNameTargets(
        string value,
        ICollection<TargetSpec> targets,
        ISet<string> seen)
    {
        foreach (var target in SplitTargets(value))
        {
            AddTarget(target, "computer-name", targets, seen);
        }
    }

    private static void AddTargetFileTargets(
        string targetFile,
        ICollection<TargetSpec> targets,
        ISet<string> seen,
        ICollection<DispatchValidationError> errors)
    {
        if (!File.Exists(targetFile))
        {
            errors.Add(new("TargetFileNotFound", $"Target file '{targetFile}' does not exist."));
            return;
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(targetFile))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var target in SplitTargets(trimmed))
            {
                AddTarget(target, $"target-file:{targetFile}:{lineNumber}", targets, seen);
            }
        }
    }

    private static IEnumerable<string> SplitTargets(string value) =>
        value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static target => !string.IsNullOrWhiteSpace(target));

    private static void AddTarget(
        string target,
        string source,
        ICollection<TargetSpec> targets,
        ISet<string> seen)
    {
        if (!seen.Add(target))
        {
            return;
        }

        targets.Add(new TargetSpec(target, source));
    }
}
