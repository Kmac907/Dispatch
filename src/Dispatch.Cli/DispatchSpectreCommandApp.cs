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

    private sealed class RunPsCommand(DispatchCliApplication application) : AsyncCommand<RunPsSettings>
    {
        protected override async Task<int> ExecuteAsync(
            CommandContext context,
            RunPsSettings settings,
            CancellationToken cancellationToken) =>
            await application.RunCommandAsync(BuildPowerShellArgs(settings, context.Remaining.Raw), cancellationToken)
                .ConfigureAwait(false);
    }

    private sealed class RunCmdCommand : Command
    {
        protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("run cmd", "post-6 command payload enablement");
    }

    private sealed class RunExeCommand : Command
    {
        protected override int Execute(CommandContext context, CancellationToken cancellationToken) =>
            DispatchCliApplication.RenderPlannedCommand("run exe", "post-6 command payload enablement");
    }

    private sealed class RunPsSettings : CommandSettings, IRunSharedSettings
    {
        [CommandArgument(0, "<script.ps1>")]
        public string ScriptPath { get; init; } = string.Empty;

        [CommandOption("-t|--target <selector>")]
        public string? Target { get; init; }

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

        [CommandOption("--no-progress")]
        public bool NoProgress { get; init; }

        [CommandOption("--no-dashboard")]
        public bool NoDashboard { get; init; }
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

        bool NoProgress { get; }

        bool NoDashboard { get; }
    }

    private static string[] BuildPowerShellArgs(RunPsSettings settings, IReadOnlyList<string> remaining)
    {
        var mapped = new List<string> { "--script", settings.ScriptPath };
        AddSharedArgs(mapped, settings);
        AddValue(mapped, "--computer-name", settings.Target);
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

        AddValue(args, "--transport", settings.Transport);
        AddValue(args, "--expected-exit-code", settings.ExpectedExitCode);
        AddValue(args, "--throttle", settings.Throttle);
        AddValue(args, "--artifact-path", settings.ArtifactPath);
        AddValue(args, "--output-root", settings.OutputRoot);
        AddValue(args, "--remote-root", settings.RemoteRoot);
        AddValue(args, "--target-file", settings.TargetFile);
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
