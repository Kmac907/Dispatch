using Dispatch.Core;
using Spectre.Console.Cli;

namespace Dispatch.Cli;

internal sealed class DispatchSpectreCommandApp(DispatchCliApplication application)
{
    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var commandApp = new CommandApp(new DispatchTypeRegistrar(application));
        commandApp.Configure(config =>
        {
            config.ConfigureConsole(SpectreConsoleRenderer.CreateConsole(Console.Out));
            config.SetApplicationName("dispatch");
            config.SetApplicationVersion(DispatchProduct.Version);
            config.TrimTrailingPeriods(false);
            config.HideOptionDefaultValues();

            config.AddCommand<VersionCommand>("version")
                .WithDescription("Print version and build information");
            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Validate local configuration and dependencies");
            config.AddCommand<ApplyCommand>("apply")
                .WithDescription("Run a YAML job");
            config.AddCommand<PushCommand>("push")
                .WithDescription("Copy files or scripts to target hosts");

            config.AddBranch("run", run =>
            {
                run.SetDescription("Run an ad-hoc script or command");
                run.AddCommand<RunPsCommand>("ps")
                    .WithDescription("Run a PowerShell script")
                    .WithExample("run", "ps", @".\scripts\Collect-Disk.ps1", "--target", "web");
                run.AddCommand<RunCmdCommand>("cmd")
                    .WithDescription("Run a shell command");
                run.AddCommand<RunExeCommand>("exe")
                    .WithDescription("Run an executable");
            });

            config.AddBranch("hosts", hosts =>
            {
                hosts.SetDescription("Inspect, validate, and test host files");
                hosts.AddCommand<HostsListCommand>("list");
                hosts.AddCommand<HostsTestCommand>("test");
                hosts.AddCommand<HostsValidateCommand>("validate");
                hosts.AddCommand<HostsGraphCommand>("graph");
                hosts.AddCommand<HostsVarsCommand>("vars");
            });

            config.AddBranch("logs", logs =>
            {
                logs.SetDescription("Inspect run history and output");
                logs.AddCommand<LogsListCommand>("list");
                logs.AddCommand<LogsShowCommand>("show");
                logs.AddCommand<LogsTailCommand>("tail");
                logs.AddCommand<LogsExportCommand>("export");
                logs.AddCommand<LogsRetryCommand>("retry");
            });

            config.AddBranch("creds", creds =>
            {
                creds.SetDescription("Manage credential references");
                creds.AddCommand<CredsAddCommand>("add");
                creds.AddCommand<CredsListCommand>("list");
                creds.AddCommand<CredsTestCommand>("test");
                creds.AddCommand<CredsRemoveCommand>("remove");
            });

            config.AddBranch("init", init =>
            {
                init.SetDescription("Create starter files");
                init.AddCommand<InitJobCommand>("job");
                init.AddCommand<InitHostsCommand>("hosts");
                init.AddCommand<InitConfigCommand>("config");
                init.AddCommand<InitAllCommand>("all");
            });
        });

        return commandApp.RunAsync(args, cancellationToken);
    }

    private sealed class DispatchTypeRegistrar(DispatchCliApplication application) : ITypeRegistrar
    {
        public ITypeResolver Build() => new DispatchTypeResolver(application);

        public void Register(Type service, Type implementation)
        {
        }

        public void RegisterInstance(Type service, object implementation)
        {
        }

        public void RegisterLazy(Type service, Func<object> factory)
        {
        }
    }

    private sealed class DispatchTypeResolver(DispatchCliApplication application) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (type == typeof(VersionCommand))
            {
                return new VersionCommand();
            }

            if (type == typeof(DoctorCommand))
            {
                return new DoctorCommand(application);
            }

            if (type == typeof(ApplyCommand))
            {
                return new ApplyCommand();
            }

            if (type == typeof(PushCommand))
            {
                return new PushCommand();
            }

            if (type == typeof(RunPsCommand))
            {
                return new RunPsCommand(application);
            }

            if (type == typeof(RunCmdCommand))
            {
                return new RunCmdCommand();
            }

            if (type == typeof(RunExeCommand))
            {
                return new RunExeCommand();
            }

            if (PlannedCommandTypes.Contains(type))
            {
                return Activator.CreateInstance(type);
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            }

            if (typeof(CommandSettings).IsAssignableFrom(type))
            {
                return Activator.CreateInstance(type);
            }

            throw new InvalidOperationException($"No Dispatch command registration exists for '{type.FullName}'.");
        }
    }

    private sealed class VersionCommand : Command
    {
        protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderVersion();
    }

    private sealed class DoctorCommand(DispatchCliApplication application) : Command
    {
        protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
            application.RunDoctorCommand();
    }

    private sealed class ApplyCommand : Command<ApplySettings>
    {
        protected override int Execute(CommandContext context, ApplySettings settings, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("apply", "6.5 YAML Apply And Job Model");
    }

    private sealed class PushCommand : Command<PushSettings>
    {
        protected override int Execute(CommandContext context, PushSettings settings, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("push", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");
    }

    private sealed class RunPsCommand(DispatchCliApplication application) : AsyncCommand<RunPsSettings>
    {
        protected override async Task<int> ExecuteAsync(
            CommandContext context,
            RunPsSettings settings,
            CancellationToken cancellationToken) =>
            await application.RunCommandAsync(BuildPowerShellArgs(settings, context.Remaining.Raw), cancellationToken)
                .ConfigureAwait(false);
    }

    private sealed class RunCmdCommand : Command<RunCmdSettings>
    {
        protected override int Execute(CommandContext context, RunCmdSettings settings, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("run cmd", "9 PSRP Transport / 9.1 Raw WinRM Transport command execution");
    }

    private sealed class RunExeCommand : Command<RunExeSettings>
    {
        protected override int Execute(CommandContext context, RunExeSettings settings, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("run exe", "9 PSRP Transport / 9.1 Raw WinRM Transport command execution");
    }

    private abstract class PlannedCommand(string command, string roadmapItem) : Command
    {
        protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand(command, roadmapItem);
    }

    private sealed class HostsListCommand() : PlannedCommand("hosts list", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class HostsTestCommand() : PlannedCommand("hosts test", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class HostsValidateCommand() : PlannedCommand("hosts validate", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class HostsGraphCommand() : PlannedCommand("hosts graph", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class HostsVarsCommand() : PlannedCommand("hosts vars", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class LogsListCommand() : PlannedCommand("logs list", "6.3 Structured Run Logs And Log Commands");

    private sealed class LogsShowCommand() : PlannedCommand("logs show", "6.3 Structured Run Logs And Log Commands");

    private sealed class LogsTailCommand() : PlannedCommand("logs tail", "6.3 Structured Run Logs And Log Commands");

    private sealed class LogsExportCommand() : PlannedCommand("logs export", "6.3 Structured Run Logs And Log Commands");

    private sealed class LogsRetryCommand() : PlannedCommand("logs retry", "6.3 Structured Run Logs And Log Commands");

    private sealed class CredsAddCommand() : PlannedCommand("creds add", "6.4 Credential References");

    private sealed class CredsListCommand() : PlannedCommand("creds list", "6.4 Credential References");

    private sealed class CredsTestCommand() : PlannedCommand("creds test", "6.4 Credential References");

    private sealed class CredsRemoveCommand() : PlannedCommand("creds remove", "6.4 Credential References");

    private sealed class InitJobCommand() : PlannedCommand("init job", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class InitHostsCommand() : PlannedCommand("init hosts", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class InitConfigCommand() : PlannedCommand("init config", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class InitAllCommand() : PlannedCommand("init all", "6.6 Push, Hosts, Doctor, And Init Command Surfaces");

    private sealed class ApplySettings : CommandSettings
    {
        [CommandArgument(0, "[job.yml]")]
        public string? JobPath { get; init; }
    }

    private sealed class PushSettings : CommandSettings
    {
        [CommandArgument(0, "[source]")]
        public string? Source { get; init; }
    }

    private sealed class RunPsSettings : CommandSettings, IRunSharedSettings
    {
        [CommandArgument(0, "<script.ps1>")]
        public string ScriptPath { get; init; } = string.Empty;

        [CommandOption("-t|--target <selector>")]
        public string? Target { get; init; }

        [CommandOption("-i|--inventory <path>")]
        public string? Inventory { get; init; }

        [CommandOption("--exclude <selector>")]
        public string? Exclude { get; init; }

        [CommandOption("--plan")]
        public bool Plan { get; init; }

        [CommandOption("--system")]
        public bool System { get; init; }

        [CommandOption("--concurrency <n>")]
        public string? Concurrency { get; init; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [CommandOption("--run-as-system")]
        public bool RunAsSystem { get; init; }

        [CommandOption("--transport <name>")]
        public string? Transport { get; init; }

        [CommandOption("--expected-exit-code <codes>")]
        public string? ExpectedExitCode { get; init; }

        [CommandOption("--throttle <n>")]
        public string? Throttle { get; init; }

        [CommandOption("--artifact-path <path>")]
        public string? ArtifactPath { get; init; }

        [CommandOption("--output-root <path>")]
        public string? OutputRoot { get; init; }

        [CommandOption("--remote-root <path>")]
        public string? RemoteRoot { get; init; }

        [CommandOption("--target-file <path>")]
        public string? TargetFile { get; init; }

        [CommandOption("--output <mode>")]
        public string? Output { get; init; }

        [CommandOption("--no-color")]
        public bool NoColor { get; init; }

        [CommandOption("--no-progress")]
        public bool NoProgress { get; init; }

        [CommandOption("--no-dashboard")]
        public bool NoDashboard { get; init; }

        [CommandOption("--quiet")]
        public bool Quiet { get; init; }

        [CommandOption("-v|--verbose")]
        public bool Verbose { get; init; }

        [CommandOption("--trace")]
        public bool Trace { get; init; }
    }

    private sealed class RunCmdSettings : CommandSettings
    {
        [CommandArgument(0, "<command>")]
        public string Command { get; init; } = string.Empty;
    }

    private sealed class RunExeSettings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        public string Path { get; init; } = string.Empty;
    }

    private interface IRunSharedSettings
    {
        bool DryRun { get; }

        bool RunAsSystem { get; }

        string? Transport { get; }

        string? ExpectedExitCode { get; }

        string? Throttle { get; }

        string? ArtifactPath { get; }

        string? OutputRoot { get; }

        string? RemoteRoot { get; }

        string? TargetFile { get; }

        string? Output { get; }

        bool NoColor { get; }

        bool NoProgress { get; }

        bool NoDashboard { get; }

        bool Quiet { get; }

        bool Verbose { get; }

        bool Trace { get; }
    }

    private static readonly HashSet<Type> PlannedCommandTypes =
    [
        typeof(HostsListCommand),
        typeof(HostsTestCommand),
        typeof(HostsValidateCommand),
        typeof(HostsGraphCommand),
        typeof(HostsVarsCommand),
        typeof(LogsListCommand),
        typeof(LogsShowCommand),
        typeof(LogsTailCommand),
        typeof(LogsExportCommand),
        typeof(LogsRetryCommand),
        typeof(CredsAddCommand),
        typeof(CredsListCommand),
        typeof(CredsTestCommand),
        typeof(CredsRemoveCommand),
        typeof(InitJobCommand),
        typeof(InitHostsCommand),
        typeof(InitConfigCommand),
        typeof(InitAllCommand)
    ];

    private static string[] BuildPowerShellArgs(RunPsSettings settings, IReadOnlyList<string> remaining)
    {
        var mapped = new List<string> { "--script", settings.ScriptPath };
        AddSharedArgs(mapped, settings);
        AddValue(mapped, "--target", settings.Target);
        AddValue(mapped, "--inventory", settings.Inventory);
        AddValue(mapped, "--exclude", settings.Exclude);
        AddValue(mapped, "--throttle", settings.Concurrency);
        if (settings.Plan)
        {
            mapped.Add("--dry-run");
        }

        if (settings.System)
        {
            mapped.Add("--run-as-system");
        }

        AddRemaining(mapped, remaining);
        return mapped.ToArray();
    }

    private static void AddSharedArgs(List<string> args, IRunSharedSettings settings)
    {
        if (settings.DryRun)
        {
            args.Add("--dry-run");
        }

        if (settings.RunAsSystem)
        {
            args.Add("--run-as-system");
        }

        if (settings.NoProgress || settings.NoDashboard)
        {
            args.Add("--no-progress");
        }

        if (settings.NoColor)
        {
            args.Add("--no-color");
        }

        if (settings.Quiet)
        {
            args.Add("--quiet");
        }

        if (settings.Verbose)
        {
            args.Add("--verbose");
        }

        if (settings.Trace)
        {
            args.Add("--trace");
        }

        AddValue(args, "--transport", settings.Transport);
        AddValue(args, "--expected-exit-code", settings.ExpectedExitCode);
        AddValue(args, "--throttle", settings.Throttle);
        AddValue(args, "--artifact-path", settings.ArtifactPath);
        AddValue(args, "--output-root", settings.OutputRoot);
        AddValue(args, "--remote-root", settings.RemoteRoot);
        AddValue(args, "--target-file", settings.TargetFile);
        AddValue(args, "--output", settings.Output);
    }

    private static void AddValue(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add(name);
        args.Add(value);
    }

    private static void AddRemaining(List<string> args, IReadOnlyList<string> remaining)
    {
        if (remaining.Count == 0)
        {
            return;
        }

        args.Add("--");
        args.AddRange(remaining);
    }
}
