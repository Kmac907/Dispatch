using Dispatch.Core.Configuration;
using Dispatch.Core.Models;
using Dispatch.Core.Targeting;
using Microsoft.Extensions.Configuration;

namespace Dispatch.Cli;

internal static class DispatchApplyJobParser
{
    private static readonly HashSet<string> SupportedTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "description",
        "hosts",
        "transport",
        "credential",
        "strategy",
        "defaults",
        "vars",
        "tasks"
    };

    private static readonly HashSet<string> PlannedTaskTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ps",
        "cmd",
        "exe",
        "copy",
        "fetch",
        "wait",
        "reboot"
    };

    private static readonly HashSet<string> UnsupportedVarsSourceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "group_vars",
        "host_vars",
        "vars_files",
        "include_vars"
    };

    public static bool TryParse(
        string jobPath,
        ApplyCommandOptions options,
        DispatchRunCommandParser.DispatchRunAmbientConfig ambientConfig,
        IReadOnlyList<int> defaultExpectedExitCodes,
        out ApplyParseResult? result,
        out string error)
    {
        result = null;
        error = string.Empty;

        if (options.Plan && options.Check)
        {
            error = "--plan and --check cannot be used together.";
            return false;
        }

        if (options.Serial is <= 0)
        {
            error = "--serial must be a positive integer.";
            return false;
        }

        if (options.Diff)
        {
            error = "--diff is planned for apply but is not implemented in this apply slice.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(jobPath))
        {
            error = "apply requires <job.yml>.";
            return false;
        }

        if (!File.Exists(jobPath))
        {
            error = $"Job file '{jobPath}' does not exist.";
            return false;
        }

        if (!TryReadJob(jobPath, out var job, out error))
        {
            return false;
        }

        if (!TryParseTagFilter(options.Tags, "--tags", out var requiredTags, out error)
            || !TryParseTagFilter(options.SkipTags, "--skip-tags", out var skippedTags, out error))
        {
            return false;
        }

        var selectedTasks = job.Tasks
            .Where(task => IsTaskSelected(task.Tags, requiredTags, skippedTags))
            .ToArray();
        if (selectedTasks.Length == 0)
        {
            error = "No apply tasks match the selected tag filters.";
            return false;
        }

        var validateOnly = options.Plan || options.Check;
        if (validateOnly && !TryValidateCopyTasks(jobPath, selectedTasks, requireExistingSource: true, out error))
        {
            return false;
        }

        if (!validateOnly && selectedTasks.Any(static task => task.Type.Equals("copy", StringComparison.OrdinalIgnoreCase)))
        {
            error = "copy task execution is planned but not implemented in this apply slice. Use --plan or --check.";
            return false;
        }

        if (!TryLoadConfig(options.ConfigPath, ambientConfig, out var config, out error))
        {
            return false;
        }

        var targetSelectors = new[] { NormalizeOptional(options.Target) ?? job.Hosts };
        IReadOnlyList<string> excludeSelectors = NormalizeOptional(options.Exclude) is { } exclude
            ? new[] { exclude }
            : Array.Empty<string>();
        var inventoryPath = NormalizeOptional(options.Inventory) ?? config.Inventory;

        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(
            ComputerNameValues: [],
            TargetFile: null,
            TargetSelectors: targetSelectors,
            InventoryPath: inventoryPath,
            ExcludeSelectors: excludeSelectors));
        if (!targetResolution.IsValid)
        {
            error = string.Join(Environment.NewLine, targetResolution.Errors.Select(static item => $"{item.Code}: {item.Message}"));
            return false;
        }

        if (!TryResolveTransport(
                targetResolution,
                job.Transport,
                options.Transport,
                config.DefaultTransport ?? ambientConfig.DefaultTransport,
                out var transport,
                out error))
        {
            return false;
        }

        if (options.CredentialReference is { Length: > 0 } && string.IsNullOrWhiteSpace(options.CredentialReference))
        {
            error = "--credential requires a non-empty credential reference name.";
            return false;
        }

        var expectedExitCodes = job.ExpectedExitCodes.Count > 0
            ? job.ExpectedExitCodes
            : defaultExpectedExitCodes.Count > 0 ? defaultExpectedExitCodes : [0];
        var commands = selectedTasks
            .Select(task => CreateApplyTaskCommand(
                task,
                validateOnly,
                targetResolution.Targets,
                transport,
                options,
                expectedExitCodes,
                job))
            .ToArray();
        result = new ApplyParseResult(
            validateOnly ? options.Check ? "check" : "plan" : "execute",
            commands,
            options.OutputMode,
            options.Quiet);
        return true;
    }

    private static ApplyTaskCommand CreateApplyTaskCommand(
        ParsedApplyTask task,
        bool validateOnly,
        IReadOnlyList<TargetSpec> targets,
        TransportKind transport,
        ApplyCommandOptions options,
        IReadOnlyList<int> expectedExitCodes,
        ParsedApplyJob job)
    {
        if (task.Copy is { } copy)
        {
            return new ApplyTaskCommand(
                task.Index,
                task.Type,
                task.Value,
                task.Tags,
                null,
                new ApplyCopyTaskPlan(
                    copy.SourcePath ?? string.Empty,
                    copy.DestinationPath ?? string.Empty,
                    copy.Overwrite,
                    targets,
                    transport));
        }

        return new ApplyTaskCommand(
            task.Index,
            task.Type,
            task.Value,
            task.Tags,
            new DispatchRunCommand(
                DryRun: validateOnly,
                Payload: CreatePayload(task, job.Variables),
                Targets: targets,
                Transport: transport,
                ConfigPath: options.ConfigPath,
                ExpectedExitCodes: expectedExitCodes,
                Throttle: options.Serial ?? job.Serial,
                LocalRunRoot: null,
                RemoteRunRoot: null,
                ArtifactPaths: [],
                ScriptSecrets: [],
                CredentialReference: NormalizeOptional(options.CredentialReference) ?? job.CredentialReference,
                RunAsSystem: false,
                AllowRunAsSystem: false,
                NoDashboard: validateOnly || options.NoProgress,
                OutputMode: options.OutputMode,
                NoColor: options.NoColor,
                Quiet: options.Quiet,
                Verbose: options.Verbose || options.Trace,
                Trace: options.Trace),
            null);
    }

    private static bool TryReadJob(string jobPath, out ParsedApplyJob job, out string error)
    {
        job = new ParsedApplyJob();
        error = string.Empty;

        var sectionStack = new List<YamlPathPart>();
        var lineNumber = 0;
        var taskCount = 0;
        int? currentTaskIndent = null;
        foreach (var rawLine in File.ReadLines(jobPath))
        {
            lineNumber++;
            var line = StripComment(rawLine).TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();

            while (sectionStack.Count > 0 && sectionStack[^1].Indent >= indent)
            {
                sectionStack.RemoveAt(sectionStack.Count - 1);
            }

            if (sectionStack.Count > 0
                && sectionStack[^1].Key.Equals("tasks", StringComparison.OrdinalIgnoreCase)
                && currentTaskIndent.HasValue
                && indent > currentTaskIndent.Value)
            {
                if (!TryApplyTaskMetadata(jobPath, lineNumber, trimmed, Path.GetDirectoryName(Path.GetFullPath(jobPath))!, ref job, out error))
                {
                    return false;
                }

                continue;
            }

            if (sectionStack.Count > 0
                && sectionStack[^1].Key.Equals("tasks", StringComparison.OrdinalIgnoreCase)
                && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                taskCount++;
                currentTaskIndent = indent;
                if (!TryReadTask(jobPath, lineNumber, taskCount, trimmed[2..].Trim(), Path.GetDirectoryName(Path.GetFullPath(jobPath))!, ref job, out error))
                {
                    return false;
                }

                continue;
            }

            var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                error = $"{jobPath}:{lineNumber}: expected a YAML mapping entry.";
                return false;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                error = $"{jobPath}:{lineNumber}: empty YAML keys are not supported.";
                return false;
            }

            var path = sectionStack.Select(static part => part.Key).Append(key).ToArray();
            if (!TryValidateKey(jobPath, lineNumber, path, out error))
            {
                return false;
            }

            if (path.Length == 1 && UnsupportedVarsSourceKeys.Contains(key))
            {
                error = $"{jobPath}:{lineNumber}: vars source '{key}' is not supported in v1 jobs. Use inline job.vars only.";
                return false;
            }

            if (path.Length == 1 && !SupportedTopLevelKeys.Contains(key))
            {
                error = $"{jobPath}:{lineNumber}: unsupported job field '{key}'.";
                return false;
            }

            if (value.Length == 0)
            {
                if (path.Length > 1 && path[0].Equals("vars", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryValidateJobVariablePath(jobPath, lineNumber, path, out error))
                    {
                        return false;
                    }

                    error = $"{jobPath}:{lineNumber}: job.vars supports scalar task input values only in this apply slice.";
                    return false;
                }

                sectionStack.Add(new YamlPathPart(indent, key));
                continue;
            }

            if (!TryApplyScalar(jobPath, lineNumber, path, value, ref job, out error))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(job.Hosts))
        {
            error = "Job field 'hosts' is required.";
            return false;
        }

        if (job.Tasks.Count == 0)
        {
            error = "This apply slice requires at least one tasks entry with 'ps: <script.ps1>', 'cmd: <command>', or 'exe: <command>'.";
            return false;
        }

        return true;
    }

    private static bool TryApplyTaskMetadata(
        string jobPath,
        int lineNumber,
        string taskField,
        string jobDirectory,
        ref ParsedApplyJob job,
        out string error)
    {
        error = string.Empty;
        if (taskField.StartsWith("- ", StringComparison.Ordinal))
        {
            error = $"{jobPath}:{lineNumber}: task tags must use inline syntax such as 'tags: [prod, fix]' in this apply slice.";
            return false;
        }

        var separator = taskField.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            error = $"{jobPath}:{lineNumber}: expected task metadata syntax 'tags: [name]'.";
            return false;
        }

        var key = taskField[..separator].Trim();
        var value = taskField[(separator + 1)..].Trim();
        if (!TryValidateKey(jobPath, lineNumber, [key], out error))
        {
            return false;
        }

        if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseTagList(value, out var tags))
            {
                error = $"{jobPath}:{lineNumber}: task tags must contain one or more names.";
                return false;
            }

            var tasks = job.Tasks.ToArray();
            if (tasks.Length == 0)
            {
                error = $"{jobPath}:{lineNumber}: task metadata must follow a task entry.";
                return false;
            }

            tasks[^1] = tasks[^1] with { Tags = tags };
            job = job with { Tasks = tasks };
            return true;
        }

        var existingTasks = job.Tasks.ToArray();
        if (existingTasks.Length == 0)
        {
            error = $"{jobPath}:{lineNumber}: task metadata must follow a task entry.";
            return false;
        }

        if (!existingTasks[^1].Type.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{jobPath}:{lineNumber}: task field '{key}' is not supported in this apply slice.";
            return false;
        }

        if (!TryApplyCopyTaskField(
                jobPath,
                lineNumber,
                key,
                value,
                jobDirectory,
                existingTasks[^1],
                out var updatedTask,
                out error))
        {
            return false;
        }

        existingTasks[^1] = updatedTask;
        job = job with { Tasks = existingTasks };
        return true;
    }

    private static bool TryReadTask(
        string jobPath,
        int lineNumber,
        int taskIndex,
        string task,
        string jobDirectory,
        ref ParsedApplyJob job,
        out string error)
    {
        error = string.Empty;
        var separator = task.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            error = $"{jobPath}:{lineNumber}: expected task syntax such as '- ps: <script.ps1>', '- cmd: <command>', or '- exe: <command>'.";
            return false;
        }

        var taskType = task[..separator].Trim();
        var value = NormalizeScalar(task[(separator + 1)..].Trim());
        if (!PlannedTaskTypes.Contains(taskType))
        {
            error = $"{jobPath}:{lineNumber}: unsupported task type '{taskType}'.";
            return false;
        }

        if (!taskType.Equals("ps", StringComparison.OrdinalIgnoreCase)
            && !taskType.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            && !taskType.Equals("exe", StringComparison.OrdinalIgnoreCase)
            && !taskType.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{jobPath}:{lineNumber}: task type '{taskType}' is planned but not implemented in this apply slice.";
            return false;
        }

        if (taskType.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadCopyTask(jobPath, lineNumber, taskIndex, value, jobDirectory, out var copyTask, out error))
            {
                return false;
            }

            job = job with { Tasks = [.. job.Tasks, copyTask] };
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = taskType.ToLowerInvariant() switch
            {
                "cmd" => $"{jobPath}:{lineNumber}: cmd task requires a command line.",
                "exe" => $"{jobPath}:{lineNumber}: exe task requires a command line.",
                _ => $"{jobPath}:{lineNumber}: ps task requires a script path."
            };
            return false;
        }

        var taskValue = taskType.Equals("ps", StringComparison.OrdinalIgnoreCase)
            ? Path.IsPathRooted(value)
                ? value
                : Path.GetFullPath(Path.Combine(jobDirectory, value))
            : value;
        job = job with
        {
            Tasks = [.. job.Tasks, new ParsedApplyTask(taskIndex, taskType.ToLowerInvariant(), taskValue, [], null)]
        };
        return true;
    }

    private static bool TryReadCopyTask(
        string jobPath,
        int lineNumber,
        int taskIndex,
        string value,
        string jobDirectory,
        out ParsedApplyTask task,
        out string error)
    {
        task = new ParsedApplyTask(taskIndex, "copy", string.Empty, [], null);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!value.StartsWith("{", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal))
        {
            error = $"{jobPath}:{lineNumber}: copy task must use mapping syntax with src and dest fields.";
            return false;
        }

        var body = value[1..^1].Trim();
        if (body.Length == 0)
        {
            error = $"{jobPath}:{lineNumber}: copy task requires src and dest fields.";
            return false;
        }

        if (!TrySplitInlineMapEntries(body, out var items))
        {
            error = $"{jobPath}:{lineNumber}: copy task inline entries must use balanced quotes.";
            return false;
        }

        foreach (var item in items)
        {
            var separator = IndexOfInlineMapSeparator(item);
            if (separator <= 0)
            {
                error = $"{jobPath}:{lineNumber}: copy task inline entries must use 'name: value' syntax.";
                return false;
            }

            var key = item[..separator].Trim();
            var rawValue = item[(separator + 1)..].Trim();
            if (!TryApplyCopyTaskField(jobPath, lineNumber, key, rawValue, jobDirectory, task, out task, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyCopyTaskField(
        string jobPath,
        int lineNumber,
        string key,
        string rawValue,
        string jobDirectory,
        ParsedApplyTask task,
        out ParsedApplyTask updatedTask,
        out string error)
    {
        updatedTask = task;
        error = string.Empty;
        var value = NormalizeScalar(rawValue);
        var copy = task.Copy ?? new ParsedApplyCopyTask(null, null, false);
        switch (key.ToLowerInvariant())
        {
            case "src":
            case "source":
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = $"{jobPath}:{lineNumber}: copy task src requires a local file path.";
                    return false;
                }

                var sourcePath = Path.IsPathRooted(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(jobDirectory, value));

                updatedTask = task with { Copy = copy with { SourcePath = sourcePath } };
                return true;
            case "dest":
            case "destination":
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = $"{jobPath}:{lineNumber}: copy task dest requires a remote destination path.";
                    return false;
                }

                if (!IsRootedWindowsPath(value))
                {
                    error = $"{jobPath}:{lineNumber}: copy task dest '{value}' must be a rooted Windows path.";
                    return false;
                }

                updatedTask = task with { Copy = copy with { DestinationPath = value } };
                return true;
            case "overwrite":
                if (!bool.TryParse(value, out var overwrite))
                {
                    error = $"{jobPath}:{lineNumber}: copy task overwrite must be true or false.";
                    return false;
                }

                updatedTask = task with { Copy = copy with { Overwrite = overwrite } };
                return true;
            default:
                error = $"{jobPath}:{lineNumber}: copy task field '{key}' is not supported in this apply slice.";
                return false;
        }
    }

    private static bool TryValidateCopyTasks(
        string jobPath,
        IReadOnlyList<ParsedApplyTask> tasks,
        bool requireExistingSource,
        out string error)
    {
        foreach (var task in tasks.Where(static task => task.Type.Equals("copy", StringComparison.OrdinalIgnoreCase)))
        {
            if (task.Copy is null || string.IsNullOrWhiteSpace(task.Copy.SourcePath))
            {
                error = $"{jobPath}: copy task {task.Index} requires src.";
                return false;
            }

            if (requireExistingSource && !File.Exists(task.Copy.SourcePath))
            {
                error = $"{jobPath}: copy task {task.Index} source file '{task.Copy.SourcePath}' does not exist.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(task.Copy.DestinationPath))
            {
                error = $"{jobPath}: copy task {task.Index} requires dest.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsRootedWindowsPath(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal)
        || (value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/'));

    private static DispatchPayload CreatePayload(ParsedApplyTask task, IReadOnlyList<ParsedApplyVariable> variables) =>
        task.Type is "cmd" or "exe"
            ? new CommandPayload(task.Value, task.Type, null)
            : new ScriptPayload(task.Value, BuildJobVariableArguments(variables));

    private static bool TryParseTagFilter(
        string? value,
        string optionName,
        out IReadOnlyList<string> tags,
        out string error)
    {
        tags = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!TryParseTagList(value, out tags))
        {
            error = $"{optionName} requires one or more tag names.";
            return false;
        }

        return true;
    }

    private static bool TryParseTagList(string value, out IReadOnlyList<string> tags)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized[1..^1];
        }

        var parsed = new List<string>();
        foreach (var item in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = NormalizeScalar(item);
            if (string.IsNullOrWhiteSpace(tag))
            {
                tags = [];
                return false;
            }

            parsed.Add(tag);
        }

        tags = parsed;
        return parsed.Count > 0;
    }

    private static bool IsTaskSelected(
        IReadOnlyList<string> taskTags,
        IReadOnlyList<string> requiredTags,
        IReadOnlyList<string> skippedTags)
    {
        if (requiredTags.Count > 0
            && !taskTags.Any(taskTag => requiredTags.Contains(taskTag, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return skippedTags.Count == 0
            || !taskTags.Any(taskTag => skippedTags.Contains(taskTag, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryApplyScalar(
        string jobPath,
        int lineNumber,
        IReadOnlyList<string> path,
        string value,
        ref ParsedApplyJob job,
        out string error)
    {
        error = string.Empty;
        var normalized = NormalizeScalar(value);
        if (path.Count == 1)
        {
            switch (path[0].ToLowerInvariant())
            {
                case "hosts":
                    job = job with { Hosts = NormalizeSelector(normalized) };
                    return true;
                case "transport":
                    job = job with { Transport = normalized };
                    return true;
                case "credential":
                    job = job with { CredentialReference = normalized };
                    return true;
                case "name":
                case "description":
                    return true;
                case "vars":
                    if (!TryParseInlineJobVariables(jobPath, lineNumber, normalized, ref job, out error))
                    {
                        return false;
                    }

                    return true;
                default:
                    error = $"{jobPath}:{lineNumber}: field '{path[0]}' must be a mapping in this apply slice.";
                    return false;
            }
        }

        if (path.Count == 2
            && path[0].Equals("defaults", StringComparison.OrdinalIgnoreCase)
            && path[1].Equals("expected_exit_codes", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseExpectedExitCodes(normalized, out var expectedExitCodes))
            {
                error = $"{jobPath}:{lineNumber}: defaults.expected_exit_codes must contain one or more integers.";
                return false;
            }

            job = job with { ExpectedExitCodes = expectedExitCodes };
            return true;
        }

        if (path.Count == 2
            && path[0].Equals("strategy", StringComparison.OrdinalIgnoreCase)
            && path[1].Equals("serial", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(normalized, out var serial) || serial <= 0)
            {
                error = $"{jobPath}:{lineNumber}: strategy.serial must be a positive integer.";
                return false;
            }

            job = job with { Serial = serial };
            return true;
        }

        if (path[0].Equals("vars", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryValidateJobVariablePath(jobPath, lineNumber, path, out error))
            {
                return false;
            }

            if (path.Count != 2)
            {
                error = $"{jobPath}:{lineNumber}: job.vars supports scalar task input values only in this apply slice.";
                return false;
            }

            if (IsInlineYamlCollection(value))
            {
                error = $"{jobPath}:{lineNumber}: job.vars supports scalar task input values only in this apply slice.";
                return false;
            }

            job = job with { Variables = [.. job.Variables, new ParsedApplyVariable(path[1], normalized)] };
            return true;
        }

        error = $"{jobPath}:{lineNumber}: unsupported job field '{string.Join('.', path)}'.";
        return false;
    }

    private static bool TryValidateKey(string jobPath, int lineNumber, IReadOnlyList<string> path, out string error)
    {
        error = string.Empty;
        var key = path[^1];
        if (key.Equals("password", StringComparison.OrdinalIgnoreCase)
            || key.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || key.Equals("token", StringComparison.OrdinalIgnoreCase)
            || key.Equals("sas", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Secret", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Token", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{jobPath}:{lineNumber}: plaintext secret field '{key}' is not allowed.";
            return false;
        }

        return true;
    }

    private static bool TryValidateJobVariablePath(
        string jobPath,
        int lineNumber,
        IReadOnlyList<string> path,
        out string error)
    {
        error = string.Empty;
        if (path.Any(static part => UnsupportedVarsSourceKeys.Contains(part)))
        {
            error = $"{jobPath}:{lineNumber}: vars source '{path[^1]}' is not supported in v1 jobs. Use inline job.vars only.";
            return false;
        }

        if (path.Any(static part => part.Equals("transport", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"{jobPath}:{lineNumber}: transport is a first-class job field and is not allowed under job.vars.";
            return false;
        }

        if (path.Count >= 2 && !IsSupportedJobVariableName(path[1]))
        {
            error = $"{jobPath}:{lineNumber}: job.vars key '{path[1]}' is not supported in this apply slice. Use letters, numbers, or underscores, starting with a letter or underscore.";
            return false;
        }

        return true;
    }

    private static bool IsSupportedJobVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        return name.All(static character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static bool TryParseInlineJobVariables(
        string jobPath,
        int lineNumber,
        string value,
        ref ParsedApplyJob job,
        out string error)
    {
        error = string.Empty;
        if (!value.StartsWith("{", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal))
        {
            error = $"{jobPath}:{lineNumber}: field 'vars' must be a mapping in this apply slice.";
            return false;
        }

        var body = value[1..^1].Trim();
        if (body.Length == 0)
        {
            return true;
        }

        if (!TrySplitInlineMapEntries(body, out var items))
        {
            error = $"{jobPath}:{lineNumber}: job.vars inline entries must use balanced quotes.";
            return false;
        }

        foreach (var item in items)
        {
            var separator = IndexOfInlineMapSeparator(item);
            if (separator <= 0)
            {
                error = $"{jobPath}:{lineNumber}: job.vars inline entries must use 'name: value' syntax.";
                return false;
            }

            var key = item[..separator].Trim();
            var rawVariableValue = item[(separator + 1)..].Trim();
            if (IsInlineYamlCollection(rawVariableValue))
            {
                error = $"{jobPath}:{lineNumber}: job.vars supports scalar task input values only in this apply slice.";
                return false;
            }

            var variableValue = NormalizeScalar(rawVariableValue);
            if (!TryValidateKey(jobPath, lineNumber, ["vars", key], out error)
                || !TryValidateJobVariablePath(jobPath, lineNumber, ["vars", key], out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                error = $"{jobPath}:{lineNumber}: job.vars entries require non-empty names.";
                return false;
            }

            job = job with { Variables = [.. job.Variables, new ParsedApplyVariable(key, variableValue)] };
        }

        return true;
    }

    private static bool TrySplitInlineMapEntries(string value, out IReadOnlyList<string> entries)
    {
        var parsed = new List<string>();
        var start = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == ',' && !inSingleQuote && !inDoubleQuote)
            {
                var entry = value[start..index].Trim();
                if (entry.Length > 0)
                {
                    parsed.Add(entry);
                }

                start = index + 1;
            }
        }

        if (inSingleQuote || inDoubleQuote)
        {
            entries = [];
            return false;
        }

        var finalEntry = value[start..].Trim();
        if (finalEntry.Length > 0)
        {
            parsed.Add(finalEntry);
        }

        entries = parsed;
        return true;
    }

    private static int IndexOfInlineMapSeparator(string value)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == ':' && !inSingleQuote && !inDoubleQuote)
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> BuildJobVariableArguments(IReadOnlyList<ParsedApplyVariable> variables)
    {
        if (variables.Count == 0)
        {
            return [];
        }

        var arguments = new List<string>(variables.Count * 2);
        foreach (var variable in variables)
        {
            arguments.Add($"-{variable.Name}");
            arguments.Add(variable.Value);
        }

        return arguments;
    }

    private static bool IsInlineYamlCollection(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static bool TryLoadConfig(
        string? configPath,
        DispatchRunCommandParser.DispatchRunAmbientConfig ambientConfig,
        out DispatchRunConfig config,
        out string error)
    {
        config = new DispatchRunConfig
        {
            Inventory = ambientConfig.Inventory,
            DefaultTransport = ambientConfig.DefaultTransport
        };
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configPath))
        {
            return true;
        }

        if (!File.Exists(configPath))
        {
            error = $"Config file '{configPath}' does not exist.";
            return false;
        }

        try
        {
            var configuration = DispatchConfigFileReader.Load(configPath);
            var section = configuration.GetSection(DispatchOptions.SectionName);
            config = config with { Inventory = section["Inventory"] ?? config.Inventory };

            var configuredTransport = section["DefaultTransport"];
            if (!string.IsNullOrWhiteSpace(configuredTransport))
            {
                if (!TryParseTransport(configuredTransport, out var parsedTransport) || parsedTransport is null)
                {
                    error = $"Config file '{configPath}' contains unsupported transport '{configuredTransport}'.";
                    return false;
                }

                config = config with { DefaultTransport = parsedTransport.Value };
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            error = $"Config file '{configPath}' is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveTransport(
        TargetResolutionResult targetResolution,
        string? jobTransport,
        string? optionTransport,
        TransportKind defaultTransport,
        out TransportKind transport,
        out string error)
    {
        transport = defaultTransport;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(optionTransport))
        {
            if (!TryParseTransport(optionTransport, out var parsed))
            {
                error = $"Unsupported transport '{optionTransport}'.";
                return false;
            }

            if (parsed is not null)
            {
                transport = parsed.Value;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(jobTransport))
        {
            if (!TryParseTransport(jobTransport, out var parsed))
            {
                error = $"Unsupported transport '{jobTransport}'.";
                return false;
            }

            if (parsed is not null)
            {
                transport = parsed.Value;
                return true;
            }
        }

        var effectiveTargetsByTransport = new Dictionary<TransportKind, List<string>>();
        foreach (var target in targetResolution.Targets)
        {
            var effectiveTransport = targetResolution.InventoryTransportPolicies?.TryGetValue(target.Name, out var inventoryTransport) == true
                                     && inventoryTransport is not null
                ? inventoryTransport.Value
                : defaultTransport;
            if (!effectiveTargetsByTransport.TryGetValue(effectiveTransport, out var targets))
            {
                targets = [];
                effectiveTargetsByTransport[effectiveTransport] = targets;
            }

            targets.Add(target.Name);
        }

        if (effectiveTargetsByTransport.Count <= 1)
        {
            transport = effectiveTargetsByTransport.Keys.SingleOrDefault(defaultTransport);
            return true;
        }

        var transports = effectiveTargetsByTransport
            .OrderBy(static entry => entry.Key.ToDispatchString(), StringComparer.Ordinal)
            .Select(static entry => $"'{entry.Key.ToDispatchString()}' for [{string.Join(", ", entry.Value)}]");
        error = $"InventoryTransportConflict: Selected targets resolved conflicting transport policies {string.Join(" and ", transports)}. Use --transport to override or align the inventory transport settings.";
        return false;
    }

    private static bool TryParseTransport(string value, out TransportKind? transport)
    {
        transport = value.Trim().ToLowerInvariant() switch
        {
            "auto" => null,
            "psexec" => TransportKind.PsExec,
            "psrp" => TransportKind.Psrp,
            "winrm" => TransportKind.WinRm,
            _ => null
        };

        return value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("psexec", StringComparison.OrdinalIgnoreCase)
            || value.Equals("psrp", StringComparison.OrdinalIgnoreCase)
            || value.Equals("winrm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseExpectedExitCodes(string value, out IReadOnlyList<int> expectedExitCodes)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized[1..^1];
        }

        var parsed = new List<int>();
        foreach (var item in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, out var exitCode))
            {
                expectedExitCodes = [];
                return false;
            }

            parsed.Add(exitCode);
        }

        expectedExitCodes = parsed;
        return parsed.Count > 0;
    }

    private static string NormalizeSelector(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized[1..^1];
        }

        return normalized;
    }

    private static string NormalizeScalar(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length >= 2
            && ((normalized[0] == '"' && normalized[^1] == '"')
                || (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            return normalized[1..^1];
        }

        return normalized;
    }

    private static string StripComment(string value)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == '#' && !inSingleQuote && !inDoubleQuote)
            {
                return value[..index];
            }
        }

        return value;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record ApplyCommandOptions(
        bool Plan,
        bool Check,
        string? ConfigPath,
        string? CredentialReference,
        string? Transport,
        string? Inventory,
        string? Target,
        string? Exclude,
        string? Tags,
        string? SkipTags,
        int? Serial,
        bool Diff,
        DispatchOutputMode OutputMode,
        bool NoColor,
        bool NoProgress,
        bool Quiet,
        bool Verbose,
        bool Trace);

    internal sealed record ApplyParseResult(
        string Mode,
        IReadOnlyList<ApplyTaskCommand> Tasks,
        DispatchOutputMode OutputMode,
        bool Quiet);

    internal sealed record ApplyTaskCommand(
        int Index,
        string Type,
        string Value,
        IReadOnlyList<string> Tags,
        DispatchRunCommand? Command,
        ApplyCopyTaskPlan? Copy);

    internal sealed record ApplyCopyTaskPlan(
        string SourcePath,
        string DestinationPath,
        bool Overwrite,
        IReadOnlyList<TargetSpec> Targets,
        TransportKind Transport);

    private sealed record ParsedApplyJob
    {
        public string Hosts { get; init; } = string.Empty;

        public string? Transport { get; init; }

        public string? CredentialReference { get; init; }

        public IReadOnlyList<int> ExpectedExitCodes { get; init; } = [];

        public int? Serial { get; init; }

        public IReadOnlyList<ParsedApplyVariable> Variables { get; init; } = [];

        public IReadOnlyList<ParsedApplyTask> Tasks { get; init; } = [];
    }

    private sealed record ParsedApplyVariable(
        string Name,
        string Value);

    private sealed record ParsedApplyTask(
        int Index,
        string Type,
        string Value,
        IReadOnlyList<string> Tags,
        ParsedApplyCopyTask? Copy);

    internal sealed record ParsedApplyCopyTask(
        string? SourcePath,
        string? DestinationPath,
        bool Overwrite);

    private sealed record DispatchRunConfig
    {
        public string? Inventory { get; init; }

        public TransportKind? DefaultTransport { get; init; }
    }

    private sealed record YamlPathPart(int Indent, string Key);
}
