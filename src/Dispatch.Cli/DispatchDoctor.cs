using Dispatch.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace Dispatch.Cli;

public interface IDispatchDoctor
{
    DispatchDoctorReport Run(DispatchDoctorRequest request);
}

public sealed class DispatchDoctor : IDispatchDoctor
{
    private readonly IOptions<DispatchOptions> options;
    private readonly IPsExecEulaStateReader psexecEulaStateReader;

    public DispatchDoctor(IOptions<DispatchOptions> options)
        : this(options, RegistryPsExecEulaStateReader.Instance)
    {
    }

    internal DispatchDoctor(IOptions<DispatchOptions> options, IPsExecEulaStateReader psexecEulaStateReader)
    {
        this.options = options;
        this.psexecEulaStateReader = psexecEulaStateReader;
    }

    public DispatchDoctorReport Run() => Run(DispatchDoctorRequest.Auto);

    public DispatchDoctorReport Run(DispatchDoctorRequest request)
    {
        var checks = new List<DispatchDoctorCheck>
        {
            CheckOperatingSystem(),
            CheckDotNetRuntime(),
            CheckPowerShell(),
            CheckLocalRunRoot(),
            CheckRunHistoryLayout(),
            CheckTransportScope(request.Transport),
            CheckCurrentUserContext(),
            CheckPolicyRestrictions()
        };

        if (request.Transport is DispatchDoctorTransportScope.Auto or DispatchDoctorTransportScope.PsExec)
        {
            checks.Add(CheckPsExec());
            checks.Add(CheckPsExecLocalPolicy());
            checks.Add(CheckPsExecEula());
            checks.Add(CheckAdminContext());
        }

        if (request.Transport is DispatchDoctorTransportScope.Auto
            or DispatchDoctorTransportScope.Psrp
            or DispatchDoctorTransportScope.WinRm)
        {
            checks.Add(CheckWinRmClient());
        }

        return new DispatchDoctorReport(checks);
    }

    private static DispatchDoctorCheck CheckDotNetRuntime()
    {
        var framework = RuntimeInformation.FrameworkDescription;
        return string.IsNullOrWhiteSpace(framework)
            ? DispatchDoctorCheck.Warning(".NET runtime", ".NET runtime description was not available.", "Dispatch requires the configured .NET runtime to start.")
            : DispatchDoctorCheck.Pass(".NET runtime", ".NET runtime is available.", framework);
    }

    private static DispatchDoctorCheck CheckOperatingSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DispatchDoctorCheck.Fail(
                "Operating system",
                "Dispatch v1 is supported on Windows admin workstations.",
                RuntimeInformation.OSDescription);
        }

        return DispatchDoctorCheck.Pass("Operating system", "Windows host detected.", RuntimeInformation.OSDescription);
    }

    private static DispatchDoctorCheck CheckPowerShell()
    {
        var powershell = ResolveExecutable("powershell.exe");
        return powershell is null
            ? DispatchDoctorCheck.Fail("PowerShell", "powershell.exe was not found in PATH.", "Required for v1 direct script execution.")
            : DispatchDoctorCheck.Pass("PowerShell", "powershell.exe is available.", RedactPath(powershell));
    }

    private DispatchDoctorCheck CheckPsExec()
    {
        var configuredPath = string.IsNullOrWhiteSpace(options.Value.PsExecPath)
            ? "psexec.exe"
            : options.Value.PsExecPath;

        var resolved = ResolveExecutable(configuredPath);
        return resolved is null
            ? DispatchDoctorCheck.Fail(
                "PsExec",
                $"PsExec was not found from configured path '{RedactPath(configuredPath)}'.",
                "Set Dispatch:PsExecPath or add psexec.exe to PATH.")
            : DispatchDoctorCheck.Pass("PsExec", "PsExec executable resolved.", RedactPath(resolved));
    }

    private static DispatchDoctorCheck CheckWinRmClient()
    {
        var resolved = ResolveAnyExecutable("winrm.exe", "winrm.cmd");
        return resolved is null
            ? DispatchDoctorCheck.Fail(
                "WinRM client",
                "WinRM command-line client was not found in PATH.",
                "Required for raw WinRM and PSRP local diagnostics.")
            : DispatchDoctorCheck.Pass("WinRM client", "WinRM command-line client is available.", RedactPath(resolved));
    }

    private DispatchDoctorCheck CheckLocalRunRoot()
    {
        var localRunRoot = options.Value.LocalRunRoot;
        if (string.IsNullOrWhiteSpace(localRunRoot))
        {
            return DispatchDoctorCheck.Fail("Output path", "Local run root is not configured.", "Set Dispatch:LocalRunRoot.");
        }

        try
        {
            var fullPath = Path.GetFullPath(localRunRoot);
            if (File.Exists(fullPath))
            {
                return DispatchDoctorCheck.Fail("Output path", "Local run root points to a file.", RedactPath(fullPath));
            }

            Directory.CreateDirectory(fullPath);
            var probePath = Path.Combine(fullPath, $".dispatch-doctor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "dispatch-doctor");
            File.Delete(probePath);

            return DispatchDoctorCheck.Pass("Output path", "Local run root is writable.", RedactPath(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return DispatchDoctorCheck.Fail(
                "Output path",
                "Local run root is not writable.",
                $"{RedactPath(localRunRoot)}: {exception.Message}");
        }
    }

    private DispatchDoctorCheck CheckRunHistoryLayout()
    {
        var localRunRoot = options.Value.LocalRunRoot;
        if (string.IsNullOrWhiteSpace(localRunRoot))
        {
            return DispatchDoctorCheck.Fail("Run-history layout", "Local run root is not configured.", "Set Dispatch:LocalRunRoot.");
        }

        string? probeRunRoot = null;
        try
        {
            var fullPath = Path.GetFullPath(localRunRoot);
            if (File.Exists(fullPath))
            {
                return DispatchDoctorCheck.Fail("Run-history layout", "Local run root points to a file.", RedactPath(fullPath));
            }

            probeRunRoot = Path.Combine(fullPath, $".dispatch-doctor-layout-{Guid.NewGuid():N}");
            var adminRoot = Path.Combine(probeRunRoot, "Admin");
            var targetsRoot = Path.Combine(probeRunRoot, "Targets");
            Directory.CreateDirectory(adminRoot);
            Directory.CreateDirectory(targetsRoot);

            var resultsPath = Path.Combine(adminRoot, "results.json");
            var eventsPath = Path.Combine(adminRoot, "events.ndjson");
            File.WriteAllText(resultsPath, "{}");
            File.WriteAllText(eventsPath, string.Empty);
            File.Delete(resultsPath);
            File.Delete(eventsPath);

            return DispatchDoctorCheck.Pass(
                "Run-history layout",
                "Run-history Admin and Targets layout is writable.",
                $"root={RedactPath(fullPath)}; admin=Admin; targets=Targets; results=Admin{Path.DirectorySeparatorChar}results.json; events=Admin{Path.DirectorySeparatorChar}events.ndjson");
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return DispatchDoctorCheck.Fail(
                "Run-history layout",
                "Run-history layout could not be created under the local run root.",
                $"{RedactPath(localRunRoot)}: {exception.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(probeRunRoot) && Directory.Exists(probeRunRoot))
            {
                try
                {
                    Directory.Delete(probeRunRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static DispatchDoctorCheck CheckAdminContext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DispatchDoctorCheck.Warning(
                "Admin context",
                "Admin token check is only available on Windows.",
                "Endpoint privileges still must be validated by transport probes.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator)
            ? DispatchDoctorCheck.Pass("Admin context", "Current process is running with an administrator token.", "Endpoint rights are still validated per target.")
            : DispatchDoctorCheck.Warning("Admin context", "Current process is not elevated.", "PsExec/admin-share operations may fail without appropriate endpoint rights.");
    }

    private static DispatchDoctorCheck CheckCurrentUserContext()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        var identityName = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                identityName = WindowsIdentity.GetCurrent().Name;
            }
            catch (SystemException)
            {
                identityName = string.Empty;
            }
        }

        var detail = string.IsNullOrWhiteSpace(identityName)
            ? $@"{domain}\{user}"
            : identityName;
        return DispatchDoctorCheck.Pass("User context", "Current user context resolved.", detail);
    }

    private DispatchDoctorCheck CheckPolicyRestrictions()
    {
        var runAsSystem = options.Value.AllowRunAsSystem ? "allowed" : "blocked";
        var psexecFallback = options.Value.AllowPsExecFallback ? "allowed" : "blocked";
        return DispatchDoctorCheck.Pass(
            "Policy restrictions",
            "Effective local policy settings loaded.",
            $"LocalSystem={runAsSystem}; PsExecFallback={psexecFallback}");
    }

    private DispatchDoctorCheck CheckPsExecLocalPolicy()
    {
        var runAsSystem = options.Value.AllowRunAsSystem ? "allowed" : "blocked";
        var psexecFallback = options.Value.AllowPsExecFallback ? "allowed" : "blocked";
        return DispatchDoctorCheck.Pass(
            "PsExec local policy",
            "Effective PsExec local policy settings loaded.",
            $"explicitPsExec=allowed; LocalSystem={runAsSystem}; implicitFallback={psexecFallback}");
    }

    private DispatchDoctorCheck CheckPsExecEula()
    {
        var state = psexecEulaStateReader.Read();
        return state.Status switch
        {
            PsExecEulaStateStatus.Accepted => DispatchDoctorCheck.Pass(
                "PsExec EULA",
                "PsExec EULA acceptance is recorded for the current user.",
                state.Detail),
            PsExecEulaStateStatus.Unavailable => DispatchDoctorCheck.Warning(
                "PsExec EULA",
                "PsExec EULA registry check is only available on Windows.",
                state.Detail),
            PsExecEulaStateStatus.ReadFailed => DispatchDoctorCheck.Warning(
                "PsExec EULA",
                "PsExec EULA registry state could not be read.",
                state.Detail),
            _ => DispatchDoctorCheck.Warning(
                "PsExec EULA",
                "PsExec EULA acceptance was not detected for the current user.",
                $"{state.Detail} Doctor does not accept or remediate PsExec EULA state.")
        };
    }

    private static DispatchDoctorCheck CheckTransportScope(DispatchDoctorTransportScope transport) =>
        DispatchDoctorCheck.Pass(
            "Transport scope",
            $"Checking {FormatTransportScope(transport)} local prerequisites.",
            transport.ToDispatchString());

    private static string FormatTransportScope(DispatchDoctorTransportScope transport) =>
        transport switch
        {
            DispatchDoctorTransportScope.Auto => "all current transport paths",
            DispatchDoctorTransportScope.PsExec => "PsExec",
            DispatchDoctorTransportScope.Psrp => "PSRP",
            DispatchDoctorTransportScope.WinRm => "raw WinRM",
            _ => transport.ToString()
        };

    private static string? ResolveAnyExecutable(params string[] executables)
    {
        foreach (var executable in executables)
        {
            var resolved = ResolveExecutable(executable);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(executable))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RedactPath(string path)
    {
        var redacted = RedactKnownRoot(path, Environment.GetEnvironmentVariable("TEMP"), "%TEMP%");
        redacted = RedactKnownRoot(redacted, Environment.GetEnvironmentVariable("TMP"), "%TMP%");
        redacted = RedactKnownRoot(redacted, Path.GetTempPath(), "%TEMP%");
        redacted = RedactKnownRoot(redacted, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        return redacted;
    }

    private static string RedactKnownRoot(string path, string? root, string replacement)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return path;
        }

        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return replacement + path[root.Length..];
        }

        return path;
    }
}

internal interface IPsExecEulaStateReader
{
    PsExecEulaState Read();
}

internal sealed record PsExecEulaState(PsExecEulaStateStatus Status, string Detail);

internal enum PsExecEulaStateStatus
{
    Accepted,
    NotAccepted,
    Unavailable,
    ReadFailed
}

internal sealed class RegistryPsExecEulaStateReader : IPsExecEulaStateReader
{
    internal static RegistryPsExecEulaStateReader Instance { get; } = new();

    private const string RegistryPath = @"Software\Sysinternals\PsExec";
    private const string ValueName = "EulaAccepted";

    public PsExecEulaState Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PsExecEulaState(
                PsExecEulaStateStatus.Unavailable,
                $@"HKCU\{RegistryPath}\{ValueName} was not read.");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            var value = key?.GetValue(ValueName);
            if (IsRegistryEnabled(value))
            {
                return new PsExecEulaState(
                    PsExecEulaStateStatus.Accepted,
                    $@"HKCU\{RegistryPath}\{ValueName}=1");
            }

            var detail = value is null
                ? $@"HKCU\{RegistryPath}\{ValueName} is not set."
                : $@"HKCU\{RegistryPath}\{ValueName}={value}.";
            return new PsExecEulaState(PsExecEulaStateStatus.NotAccepted, detail);
        }
        catch (SecurityException exception)
        {
            return new PsExecEulaState(PsExecEulaStateStatus.ReadFailed, exception.Message);
        }
        catch (IOException exception)
        {
            return new PsExecEulaState(PsExecEulaStateStatus.ReadFailed, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new PsExecEulaState(PsExecEulaStateStatus.ReadFailed, exception.Message);
        }
    }

    private static bool IsRegistryEnabled(object? value) =>
        value switch
        {
            int intValue => intValue == 1,
            long longValue => longValue == 1,
            string stringValue => stringValue.Equals("1", StringComparison.OrdinalIgnoreCase)
                || stringValue.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
}

public sealed record DispatchDoctorRequest(DispatchDoctorTransportScope Transport)
{
    public static DispatchDoctorRequest Auto { get; } = new(DispatchDoctorTransportScope.Auto);
}

public enum DispatchDoctorTransportScope
{
    Auto,
    PsExec,
    Psrp,
    WinRm
}

public static class DispatchDoctorTransportScopeExtensions
{
    public static string ToDispatchString(this DispatchDoctorTransportScope transport) =>
        transport switch
        {
            DispatchDoctorTransportScope.Auto => "auto",
            DispatchDoctorTransportScope.PsExec => "psexec",
            DispatchDoctorTransportScope.Psrp => "psrp",
            DispatchDoctorTransportScope.WinRm => "winrm",
            _ => transport.ToString().ToLowerInvariant()
        };
}

public sealed record DispatchDoctorReport(IReadOnlyList<DispatchDoctorCheck> Checks)
{
    public bool Succeeded => Checks.All(static check => check.Status != DispatchDoctorStatus.Fail);
}

public sealed record DispatchDoctorCheck(
    string Name,
    DispatchDoctorStatus Status,
    string Message,
    string Detail)
{
    public static DispatchDoctorCheck Pass(string name, string message, string detail = "") =>
        new(name, DispatchDoctorStatus.Pass, message, detail);

    public static DispatchDoctorCheck Warning(string name, string message, string detail = "") =>
        new(name, DispatchDoctorStatus.Warning, message, detail);

    public static DispatchDoctorCheck Fail(string name, string message, string detail = "") =>
        new(name, DispatchDoctorStatus.Fail, message, detail);
}

public enum DispatchDoctorStatus
{
    Pass,
    Warning,
    Fail
}
