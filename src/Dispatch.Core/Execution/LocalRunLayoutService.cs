using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Execution;

internal sealed class LocalRunLayoutService : ILocalRunLayoutService
{
    public LocalRunLayoutResult Prepare(
        string localRoot,
        string runId,
        IReadOnlyList<TargetSpec> targets,
        bool createDirectories)
    {
        var localRunRoot = Path.Combine(localRoot, runId);
        var localAdminRoot = Path.Combine(localRunRoot, "Admin");
        var localTargetsRoot = Path.Combine(localRunRoot, "Targets");
        var targetLayouts = targets
            .Select(target =>
            {
                var targetRoot = Path.Combine(localTargetsRoot, SanitizePathSegment(target.Name));
                return new TargetLocalLayout(target, targetRoot, Path.Combine(targetRoot, "result.json"));
            })
            .ToArray();

        var layout = new LocalRunLayout(
            LocalRunRoot: localRunRoot,
            LocalAdminRoot: localAdminRoot,
            LocalTargetsRoot: localTargetsRoot,
            LocalResultsJsonPath: Path.Combine(localAdminRoot, "results.json"),
            LocalResultsCsvPath: Path.Combine(localAdminRoot, "results.csv"),
            LocalEventsNdjsonPath: Path.Combine(localAdminRoot, "events.ndjson"),
            Targets: targetLayouts);

        var errors = ValidateLayout(localRoot, layout);
        if (errors.Count > 0)
        {
            return new LocalRunLayoutResult(layout, errors);
        }

        if (createDirectories)
        {
            try
            {
                Directory.CreateDirectory(layout.LocalAdminRoot);
                Directory.CreateDirectory(layout.LocalTargetsRoot);
                foreach (var target in layout.Targets)
                {
                    Directory.CreateDirectory(target.LocalTargetRoot);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add(new(
                    "LocalRunLayoutCreateFailed",
                    $"Failed to create local run layout under '{layout.LocalRunRoot}': {exception.Message}"));
            }
        }

        return new LocalRunLayoutResult(layout, errors);
    }

    private static List<DispatchValidationError> ValidateLayout(string localRoot, LocalRunLayout layout)
    {
        var errors = new List<DispatchValidationError>();

        AddFileConflict(errors, "LocalRunRootConflictsWithFile", localRoot, "Local run root");
        AddFileConflict(errors, "LocalRunPathConflictsWithFile", layout.LocalRunRoot, "Planned run path");
        AddFileConflict(errors, "LocalAdminPathConflictsWithFile", layout.LocalAdminRoot, "Planned admin path");
        AddFileConflict(errors, "LocalTargetsPathConflictsWithFile", layout.LocalTargetsRoot, "Planned targets path");

        foreach (var target in layout.Targets)
        {
            AddFileConflict(
                errors,
                "LocalTargetPathConflictsWithFile",
                target.LocalTargetRoot,
                $"Planned target path for '{target.Target.Name}'");
        }

        return errors;
    }

    private static void AddFileConflict(
        ICollection<DispatchValidationError> errors,
        string code,
        string path,
        string label)
    {
        if (File.Exists(path))
        {
            errors.Add(new(code, $"{label} '{path}' exists as a file and must be a directory."));
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}
