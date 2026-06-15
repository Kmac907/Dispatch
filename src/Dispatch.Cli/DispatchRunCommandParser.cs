using Dispatch.Core.Models;
using Dispatch.Core.Targeting;

namespace Dispatch.Cli;

internal sealed class DispatchRunCommandParser
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        TransportKind defaultTransport,
        IReadOnlyList<int> defaultExpectedExitCodes,
        out DispatchRunCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;

        var dryRun = false;
        string? scriptPath = null;
        var computerNameValues = new List<string>();
        string? targetFile = null;
        var transport = defaultTransport;
        var expectedExitCodes = defaultExpectedExitCodes.Count > 0 ? defaultExpectedExitCodes : [0];
        int? throttle = null;
        string? localRunRoot = null;
        string? remoteRunRoot = null;
        var artifactPaths = new List<string>();
        var runAsSystem = false;
        var noDashboard = false;
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
                case "--script":
                    if (!TryReadValue(args, ref index, arg, out scriptPath, out error))
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
                case "--transport":
                    if (!TryReadValue(args, ref index, arg, out var transportValue, out error))
                    {
                        return false;
                    }

                    if (!TryParseTransport(transportValue, out transport))
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

        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            error = "--script is required.";
            return false;
        }

        var targetResolution = TargetResolver.Resolve(new TargetResolutionInput(computerNameValues, targetFile));
        if (!targetResolution.IsValid)
        {
            error = string.Join(Environment.NewLine, targetResolution.Errors.Select(static resolutionError => $"{resolutionError.Code}: {resolutionError.Message}"));
            return false;
        }

        command = new DispatchRunCommand(
            DryRun: dryRun,
            ScriptPath: scriptPath,
            ScriptArguments: scriptArguments,
            Targets: targetResolution.Targets,
            Transport: transport,
            ExpectedExitCodes: expectedExitCodes,
            Throttle: throttle,
            LocalRunRoot: localRunRoot,
            RemoteRunRoot: remoteRunRoot,
            ArtifactPaths: artifactPaths,
            RunAsSystem: runAsSystem,
            NoDashboard: noDashboard);
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

    private static bool TryParseTransport(string value, out TransportKind transport)
    {
        transport = value.ToLowerInvariant() switch
        {
            "psexec" => TransportKind.PsExec,
            "psrp" => TransportKind.Psrp,
            "winrm" => TransportKind.WinRm,
            _ => default
        };

        return value.Equals("psexec", StringComparison.OrdinalIgnoreCase)
            || value.Equals("psrp", StringComparison.OrdinalIgnoreCase)
            || value.Equals("winrm", StringComparison.OrdinalIgnoreCase);
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
}
