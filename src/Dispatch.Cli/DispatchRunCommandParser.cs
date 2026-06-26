using Dispatch.Core.Configuration;
using Dispatch.Core.Models;
using Dispatch.Core.Targeting;
using Microsoft.Extensions.Configuration;

namespace Dispatch.Cli;

internal sealed class DispatchRunCommandParser
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        DispatchRunAmbientConfig ambientConfig,
        IReadOnlyList<int> defaultExpectedExitCodes,
        out DispatchRunCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;

        var dryRun = false;
        string? scriptPath = null;
        string? commandLine = null;
        string? commandShell = null;
        string? workingDirectory = null;
        var computerNameValues = new List<string>();
        string? targetFile = null;
        TransportKind? transportOverride = null;
        var expectedExitCodes = defaultExpectedExitCodes.Count > 0 ? defaultExpectedExitCodes : [0];
        int? throttle = null;
        string? localRunRoot = null;
        string? remoteRunRoot = null;
        var artifactPaths = new List<string>();
        var scriptSecrets = new List<ScriptSecretReference>();
        var runAsSystem = false;
        var noDashboard = false;
        var outputMode = DispatchOutputMode.Rich;
        var noColor = false;
        var quiet = false;
        var verbose = false;
        var trace = false;
        var targetSelectors = new List<string>();
        var excludeSelectors = new List<string>();
        string? inventoryPath = null;
        string? configPath = null;
        string? credentialReference = null;
        var scriptArguments = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--run-as-system":
                    runAsSystem = true;
                    break;
                case "--no-progress":
                case "--no-dashboard":
                    noDashboard = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "-v":
                case "--verbose":
                    verbose = true;
                    break;
                case "--trace":
                    trace = true;
                    verbose = true;
                    break;
                case "--script":
                    if (!TryReadValue(args, ref index, arg, out scriptPath, out error))
                    {
                        return false;
                    }

                    break;
                case "--command":
                    if (!TryReadValue(args, ref index, arg, out commandLine, out error))
                    {
                        return false;
                    }

                    break;
                case "--shell":
                    if (!TryReadValue(args, ref index, arg, out commandShell, out error))
                    {
                        return false;
                    }

                    break;
                case "--working-directory":
                    if (!TryReadValue(args, ref index, arg, out workingDirectory, out error))
                    {
                        return false;
                    }

                    break;
                case "--computer-name":
                    if (!TryReadValue(args, ref index, arg, out var computerNameValue, out error))
                    {
                        return false;
                    }

                    computerNameValues.Add(computerNameValue);
                    break;
                case "-t":
                case "--target":
                    if (!TryReadValue(args, ref index, arg, out var targetSelector, out error))
                    {
                        return false;
                    }

                    targetSelectors.Add(targetSelector);
                    break;
                case "--exclude":
                    if (!TryReadValue(args, ref index, arg, out var excludeSelector, out error))
                    {
                        return false;
                    }

                    excludeSelectors.Add(excludeSelector);
                    break;
                case "-i":
                case "--inventory":
                    if (!TryReadValue(args, ref index, arg, out inventoryPath, out error))
                    {
                        return false;
                    }

                    break;
                case "--config":
                    if (!TryReadValue(args, ref index, arg, out configPath, out error))
                    {
                        return false;
                    }

                    break;
                case "--credential":
                    if (!TryReadValue(args, ref index, arg, out credentialReference, out error))
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(credentialReference))
                    {
                        error = "--credential requires a non-empty credential reference name.";
                        return false;
                    }

                    credentialReference = credentialReference.Trim();
                    break;
                case "--output":
                    if (!TryReadValue(args, ref index, arg, out var outputValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseOutputMode(outputValue, out outputMode))
                    {
                        error = $"Unsupported output mode '{outputValue}'. Expected rich, table, json, ndjson, or yaml.";
                        return false;
                    }

                    break;
                case "--transport":
                    if (!TryReadValue(args, ref index, arg, out var transportValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseTransport(transportValue, out transportOverride))
                    {
                        error = $"Unsupported transport '{transportValue}'.";
                        return false;
                    }

                    break;
                case "--expected-exit-code":
                    if (!TryReadValue(args, ref index, arg, out var exitCodeValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseExpectedExitCodes(exitCodeValue, out expectedExitCodes))
                    {
                        error = $"Expected exit codes must be integers: '{exitCodeValue}'.";
                        return false;
                    }

                    break;
                case "--throttle":
                    if (!TryReadValue(args, ref index, arg, out var throttleValue, out error))
                    {
                        return false;
                    }

                    if (!int.TryParse(throttleValue, out var parsedThrottle))
                    {
                        error = $"Throttle must be an integer: '{throttleValue}'.";
                        return false;
                    }

                    throttle = parsedThrottle;
                    break;
                case "--output-root":
                    if (!TryReadValue(args, ref index, arg, out localRunRoot, out error))
                    {
                        return false;
                    }

                    break;
                case "--remote-root":
                    if (!TryReadValue(args, ref index, arg, out remoteRunRoot, out error))
                    {
                        return false;
                    }

                    break;
                case "--artifact-path":
                    if (!TryReadValue(args, ref index, arg, out var artifactPathValue, out error))
                    {
                        return false;
                    }

                    artifactPaths.AddRange(SplitCommaSeparatedValues(artifactPathValue));
                    break;
                case "--secret":
                    if (!TryReadValue(args, ref index, arg, out var secretValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseScriptSecretReference(secretValue, out var scriptSecret, out error))
                    {
                        return false;
                    }

                    scriptSecrets.Add(scriptSecret);
                    break;
                case "--target-file":
                    if (!TryReadValue(args, ref index, arg, out targetFile, out error))
                    {
                        return false;
                    }

                    break;
                case "--":
                    scriptArguments.AddRange(args.Skip(index + 1));
                    index = args.Count;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{arg}'.";
                        return false;
                    }

                    scriptArguments.Add(arg);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(scriptPath) && !string.IsNullOrWhiteSpace(commandLine))
        {
            error = "Specify either --script or --command, not both.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(scriptPath) && string.IsNullOrWhiteSpace(commandLine))
        {
            error = "Either --script or --command is required.";
            return false;
        }

        DispatchPayload payload;
        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            payload = new ScriptPayload(scriptPath, scriptArguments);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(commandShell))
            {
                error = "--shell is required when using --command.";
                return false;
            }

            payload = new CommandPayload(commandLine!, commandShell, workingDirectory);
        }

        if (!TryLoadConfig(configPath, ambientConfig, out var config, out error))
        {
            return false;
        }

        inventoryPath ??= config.Inventory;
        if (targetSelectors.Count == 0 && !string.IsNullOrWhiteSpace(config.Target))
        {
            targetSelectors.Add(config.Target);
        }

        if (excludeSelectors.Count == 0 && !string.IsNullOrWhiteSpace(config.Exclude))
        {
            excludeSelectors.Add(config.Exclude);
        }

        var effectiveDefaultTransport = config.DefaultTransport ?? ambientConfig.DefaultTransport;
        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(
            computerNameValues,
            targetFile,
            targetSelectors,
            inventoryPath,
            excludeSelectors));
        if (!targetResolution.IsValid)
        {
            error = string.Join(Environment.NewLine, targetResolution.Errors.Select(static resolutionError => $"{resolutionError.Code}: {resolutionError.Message}"));
            return false;
        }

        if (!TryResolveTransport(targetResolution, transportOverride, effectiveDefaultTransport, out var resolvedTransport, out error))
        {
            return false;
        }

        command = new DispatchRunCommand(
            DryRun: dryRun,
            Payload: payload,
            Targets: targetResolution.Targets,
            Transport: resolvedTransport,
            ConfigPath: configPath,
            ExpectedExitCodes: expectedExitCodes,
            Throttle: throttle,
            LocalRunRoot: localRunRoot,
            RemoteRunRoot: remoteRunRoot,
            ArtifactPaths: artifactPaths,
            ScriptSecrets: scriptSecrets,
            CredentialReference: credentialReference,
            RunAsSystem: runAsSystem,
            NoDashboard: noDashboard,
            OutputMode: outputMode,
            NoColor: noColor,
            Quiet: quiet,
            Verbose: verbose,
            Trace: trace);
        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string value,
        out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (index + 1 >= args.Count)
        {
            error = $"{option} requires a value.";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static IEnumerable<string> SplitCommaSeparatedValues(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseScriptSecretReference(
        string value,
        out ScriptSecretReference reference,
        out string error)
    {
        reference = new ScriptSecretReference(string.Empty, string.Empty);
        error = string.Empty;

        var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            error = "--secret requires name=reference, for example --secret payload_sas=blob-install-sas.";
            return false;
        }

        reference = new ScriptSecretReference(parts[0], parts[1]);
        return true;
    }

    private static bool TryLoadConfig(
        string? configPath,
        DispatchRunAmbientConfig ambientConfig,
        out DispatchRunConfig config,
        out string error)
    {
        config = new DispatchRunConfig
        {
            Inventory = ambientConfig.Inventory,
            Target = ambientConfig.Target,
            Exclude = ambientConfig.Exclude,
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

            config = config with
            {
                Inventory = section["Inventory"] ?? config.Inventory,
                Target = section["Target"] ?? config.Target,
                Exclude = section["Exclude"] ?? config.Exclude
            };

            var configuredTransport = section["DefaultTransport"];
            if (!string.IsNullOrWhiteSpace(configuredTransport))
            {
                if (!TryParseTransport(configuredTransport, out var parsedTransport) || parsedTransport is null)
                {
                    error = $"Config file '{configPath}' contains unsupported transport '{configuredTransport}'.";
                    return false;
                }

                config = config with
                {
                    DefaultTransport = parsedTransport.Value
                };
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
        TransportKind? transportOverride,
        TransportKind defaultTransport,
        out TransportKind transport,
        out string error)
    {
        if (transportOverride is not null)
        {
            transport = transportOverride.Value;
            error = string.Empty;
            return true;
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
            error = string.Empty;
            return true;
        }

        var transports = effectiveTargetsByTransport
            .OrderBy(static entry => entry.Key.ToDispatchString(), StringComparer.Ordinal)
            .Select(static entry => $"'{entry.Key.ToDispatchString()}' for [{string.Join(", ", entry.Value)}]");
        transport = defaultTransport;
        error = $"InventoryTransportConflict: Selected targets resolved conflicting transport policies {string.Join(" and ", transports)}. Use --transport to override or align the inventory transport settings.";
        return false;
    }

    private static bool TryParseTransport(string value, out TransportKind? transport)
    {
        transport = value.ToLowerInvariant() switch
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

    private static bool TryParseOutputMode(string value, out DispatchOutputMode outputMode)
    {
        outputMode = value.ToLowerInvariant() switch
        {
            "rich" => DispatchOutputMode.Rich,
            "table" => DispatchOutputMode.Table,
            "json" => DispatchOutputMode.Json,
            "ndjson" => DispatchOutputMode.Ndjson,
            "yaml" => DispatchOutputMode.Yaml,
            _ => default
        };

        return value.Equals("rich", StringComparison.OrdinalIgnoreCase)
            || value.Equals("table", StringComparison.OrdinalIgnoreCase)
            || value.Equals("json", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ndjson", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseExpectedExitCodes(string value, out IReadOnlyList<int> exitCodes)
    {
        var parsed = new List<int>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, out var exitCode))
            {
                exitCodes = [];
                return false;
            }

            parsed.Add(exitCode);
        }

        exitCodes = parsed;
        return parsed.Count > 0;
    }

    internal sealed record DispatchRunAmbientConfig(
        string? Inventory,
        string? Target,
        string? Exclude,
        TransportKind DefaultTransport);

    private sealed record DispatchRunConfig
    {
        public string? Inventory { get; init; }

        public string? Target { get; init; }

        public string? Exclude { get; init; }

        public TransportKind? DefaultTransport { get; init; }
    }
}
