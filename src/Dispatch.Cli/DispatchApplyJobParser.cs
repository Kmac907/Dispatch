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
        out DispatchRunCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;

        if (options.Plan && options.Check)
        {
            error = "--plan and --check cannot be used together.";
            return false;
        }

        if (options.Serial.HasValue && options.Concurrency.HasValue)
        {
            error = "--serial and --concurrency cannot be used together.";
            return false;
        }

        if (options.Serial is <= 0)
        {
            error = "--serial must be a positive integer.";
            return false;
        }

        if (options.Concurrency is <= 0)
        {
            error = "--concurrency must be a positive integer.";
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

        if (!TryLoadConfig(options.ConfigPath, ambientConfig, out var config, out error))
        {
            return false;
        }

        if (!TryResolveTransport(job.Transport, options.Transport, config.DefaultTransport ?? ambientConfig.DefaultTransport, out var transport, out error))
        {
            return false;
        }

        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(
            ComputerNameValues: [],
            TargetFile: null,
            TargetSelectors: [job.Hosts],
            InventoryPath: config.Inventory,
            ExcludeSelectors: null));
        if (!targetResolution.IsValid)
        {
            error = string.Join(Environment.NewLine, targetResolution.Errors.Select(static item => $"{item.Code}: {item.Message}"));
            return false;
        }

        if (options.CredentialReference is { Length: > 0 } && string.IsNullOrWhiteSpace(options.CredentialReference))
        {
            error = "--credential requires a non-empty credential reference name.";
            return false;
        }

        var validateOnly = options.Plan || options.Check;
        command = new DispatchRunCommand(
            DryRun: validateOnly,
            Payload: new ScriptPayload(job.PowerShellScriptPath, []),
            Targets: targetResolution.Targets,
            Transport: transport,
            ConfigPath: options.ConfigPath,
            ExpectedExitCodes: job.ExpectedExitCodes.Count > 0
                ? job.ExpectedExitCodes
                : defaultExpectedExitCodes.Count > 0 ? defaultExpectedExitCodes : [0],
            Throttle: options.Serial ?? options.Concurrency ?? job.Serial,
            LocalRunRoot: null,
            RemoteRunRoot: null,
            ArtifactPaths: [],
            CredentialReference: NormalizeOptional(options.CredentialReference) ?? job.CredentialReference,
            RunAsSystem: false,
            NoDashboard: validateOnly,
            OutputMode: options.OutputMode,
            NoColor: options.NoColor,
            Quiet: false,
            Verbose: false,
            Trace: false);
        return true;
    }

    private static bool TryReadJob(string jobPath, out ParsedApplyJob job, out string error)
    {
        job = new ParsedApplyJob();
        error = string.Empty;

        var sectionStack = new List<YamlPathPart>();
        var lineNumber = 0;
        var taskCount = 0;
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
                && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                taskCount++;
                if (!TryReadTask(jobPath, lineNumber, trimmed[2..].Trim(), Path.GetDirectoryName(Path.GetFullPath(jobPath))!, ref job, out error))
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

        if (string.IsNullOrWhiteSpace(job.PowerShellScriptPath))
        {
            error = "This apply slice requires exactly one tasks entry with 'ps: <script.ps1>'.";
            return false;
        }

        if (taskCount != 1)
        {
            error = "This apply slice supports exactly one task.";
            return false;
        }

        return true;
    }

    private static bool TryReadTask(
        string jobPath,
        int lineNumber,
        string task,
        string jobDirectory,
        ref ParsedApplyJob job,
        out string error)
    {
        error = string.Empty;
        var separator = task.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            error = $"{jobPath}:{lineNumber}: expected task syntax '- ps: <script.ps1>'.";
            return false;
        }

        var taskType = task[..separator].Trim();
        var value = NormalizeScalar(task[(separator + 1)..].Trim());
        if (!PlannedTaskTypes.Contains(taskType))
        {
            error = $"{jobPath}:{lineNumber}: unsupported task type '{taskType}'.";
            return false;
        }

        if (!taskType.Equals("ps", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{jobPath}:{lineNumber}: task type '{taskType}' is planned but not implemented in this apply slice.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{jobPath}:{lineNumber}: ps task requires a script path.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(job.PowerShellScriptPath))
        {
            error = $"{jobPath}:{lineNumber}: this apply slice supports exactly one ps task.";
            return false;
        }

        job = job with
        {
            PowerShellScriptPath = Path.IsPathRooted(value)
                ? value
                : Path.GetFullPath(Path.Combine(jobDirectory, value))
        };
        return true;
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
            if (!TryParseTransport(optionTransport, out var parsed) || parsed is null)
            {
                error = $"Unsupported transport '{optionTransport}'.";
                return false;
            }

            transport = parsed.Value;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(jobTransport))
        {
            if (!TryParseTransport(jobTransport, out var parsed) || parsed is null)
            {
                error = $"Unsupported transport '{jobTransport}'.";
                return false;
            }

            transport = parsed.Value;
        }

        return true;
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
        int? Serial,
        int? Concurrency,
        DispatchOutputMode OutputMode,
        bool NoColor);

    private sealed record ParsedApplyJob
    {
        public string Hosts { get; init; } = string.Empty;

        public string? Transport { get; init; }

        public string? CredentialReference { get; init; }

        public string PowerShellScriptPath { get; init; } = string.Empty;

        public IReadOnlyList<int> ExpectedExitCodes { get; init; } = [];

        public int? Serial { get; init; }
    }

    private sealed record DispatchRunConfig
    {
        public string? Inventory { get; init; }

        public TransportKind? DefaultTransport { get; init; }
    }

    private sealed record YamlPathPart(int Indent, string Key);
}
