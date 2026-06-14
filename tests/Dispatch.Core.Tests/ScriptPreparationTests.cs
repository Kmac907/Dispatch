using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Core.Tests;

public sealed class ScriptPreparationTests
{
    [Fact]
    public void AdminSharePathConvertsDriveQualifiedRemotePath()
    {
        var result = AdminSharePath.FromRemoteWindowsPath("PC001", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1");

        Assert.True(result.IsValid);
        Assert.Equal(@"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", result.Path);
    }

    [Fact]
    public void AdminSharePathRejectsUncRemotePath()
    {
        var result = AdminSharePath.FromRemoteWindowsPath("PC001", @"\\server\share\Fix.ps1");

        Assert.False(result.IsValid);
        Assert.Equal("RemotePathMustBeDrivePath", result.Error?.Code);
    }

    [Fact]
    public async Task ScriptPreparationCopiesOnlySelectedScriptToEachTargetAdminSharePath()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var preparation = provider.GetRequiredService<IScriptPreparationService>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-Mode", "Repair"]),
            targets: [new TargetSpec("PC001"), new TargetSpec("PC002")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await preparation.PrepareAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Manifest);
        Assert.Equal(script.Path, result.Manifest.SourceScriptPath);
        Assert.Equal(["-Mode", "Repair"], result.Manifest.ScriptArguments);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script", result.Manifest.RemoteScriptDirectory);
        Assert.Equal(2, result.Manifest.Targets.Count);
        Assert.Equal(
            [@"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", @"\\PC002\C$\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1"],
            result.Manifest.Targets.Select(static target => target.AdminShareScriptPath));

        Assert.Equal(
            [@"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\script", @"\\PC002\C$\ProgramData\Dispatch\Runs\run-001\script"],
            endpointFileSystem.CreatedDirectories);
        Assert.Equal(2, endpointFileSystem.Copies.Count);
        Assert.All(endpointFileSystem.Copies, copy => Assert.Equal(script.Path, copy.SourcePath));
    }

    [Fact]
    public async Task ScriptPreparationReportsPerTargetTransferFailures()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem
        {
            ThrowOnDestinationContaining = @"\\PC002\"
        };
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var preparation = provider.GetRequiredService<IScriptPreparationService>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001"), new TargetSpec("PC002")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await preparation.PrepareAsync(plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Targets[0].Succeeded);
        Assert.False(result.Targets[1].Succeeded);
        Assert.Equal(FailureCategory.ScriptTransferFailed, result.Targets[1].FailureCategory);
        Assert.Contains("PC002", result.Targets[1].FailureMessage);
    }

    [Fact]
    public async Task ScriptPreparationDoesNotCopyForDryRunPlans()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var preparation = provider.GetRequiredService<IScriptPreparationService>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await preparation.PrepareAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(endpointFileSystem.CreatedDirectories);
        Assert.Empty(endpointFileSystem.Copies);
    }

    [Fact]
    public async Task ScriptPreparationPreservesPayloadArgumentsWithoutStagingPayloadFiles()
    {
        using var script = TemporaryScript.Create("Install-App.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var preparation = provider.GetRequiredService<IScriptPreparationService>();
        var scriptArguments = new[]
        {
            "-PackageUri",
            "https://contoso.example/packages/app.msi",
            "-PackageSource",
            @"\\fileserver\packages\app.msi"
        };
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, scriptArguments),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await preparation.PrepareAsync(plan, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Manifest);
        Assert.Equal(scriptArguments, result.Manifest.ScriptArguments);
        var copy = Assert.Single(endpointFileSystem.Copies);
        Assert.Equal(script.Path, copy.SourcePath);
        Assert.EndsWith(@"\Install-App.ps1", copy.DestinationPath);
    }

    private static ServiceProvider BuildProvider(string localRunRoot, IEndpointFileSystem endpointFileSystem)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:LocalRunRoot"] = localRunRoot,
                ["Dispatch:RemoteRunRoot"] = @"C:\ProgramData\Dispatch\Runs",
                ["Dispatch:ExpectedExitCodes:0"] = "0"
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .AddSingleton<IRunIdGenerator>(new FixedRunIdGenerator("run-001"))
            .AddSingleton<ISystemClock>(new FixedSystemClock(new DateTimeOffset(2026, 06, 13, 12, 0, 0, TimeSpan.Zero)))
            .AddSingleton(endpointFileSystem)
            .BuildServiceProvider(validateScopes: true);
    }

    private sealed class FixedRunIdGenerator(string runId) : IRunIdGenerator
    {
        public string CreateRunId() => runId;
    }

    private sealed class FixedSystemClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class RecordingEndpointFileSystem : IEndpointFileSystem
    {
        public List<string> CreatedDirectories { get; } = [];

        public List<(string SourcePath, string DestinationPath)> Copies { get; } = [];

        public string? ThrowOnDestinationContaining { get; init; }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            CreatedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            if (ThrowOnDestinationContaining is not null
                && destinationPath.Contains(ThrowOnDestinationContaining, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated copy failure.");
            }

            Copies.Add((sourcePath, destinationPath));
            return Task.CompletedTask;
        }
    }
}
