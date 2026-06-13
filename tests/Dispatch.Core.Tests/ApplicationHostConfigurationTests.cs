using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Tests;

public sealed class ApplicationHostConfigurationTests
{
    [Fact]
    public void CoreServicesResolveWithDefaultOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<IOptions<DispatchOptions>>().Value;

        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.LocalRunRoot);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(8, options.Throttle);
        Assert.Equal([0], options.ExpectedExitCodes);
        Assert.NotNull(provider.GetRequiredService<IDispatchPlanner>());
        Assert.NotNull(provider.GetRequiredService<IDispatchExecutor>());
    }

    [Fact]
    public void CoreOptionsBindFromConfiguration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Dispatch:LocalRunRoot"] = "D:\\Dispatch\\Runs",
            ["Dispatch:RemoteRunRoot"] = "D:\\RemoteDispatch",
            ["Dispatch:DefaultTransport"] = "PsExec",
            ["Dispatch:Throttle"] = "16",
            ["Dispatch:ExpectedExitCodes:0"] = "0",
            ["Dispatch:ExpectedExitCodes:1"] = "3010"
        });

        var options = provider.GetRequiredService<IOptions<DispatchOptions>>().Value;

        Assert.Equal("D:\\Dispatch\\Runs", options.LocalRunRoot);
        Assert.Equal("D:\\RemoteDispatch", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(16, options.Throttle);
        Assert.Equal([0, 3010], options.ExpectedExitCodes);
    }

    [Fact]
    public async Task PlannerAndExecutorAreRegisteredButNotImplementedYet()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();

        var request = new DispatchRequest(
            payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec);

        await Assert.ThrowsAsync<NotSupportedException>(() => planner.CreatePlanAsync(request, CancellationToken.None));

        var job = new DispatchJob(
            RunId: "run-001",
            Targets: [new TargetSpec("PC001")],
            Payload: request.Payload,
            Transport: request.Transport,
            ExecutionContext: new ExecutionContextOptions(),
            ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs", true),
            TimeoutPolicy: new TimeoutPolicy(),
            RetryPolicy: new RetryPolicy(),
            ExpectedExitCodes: [0],
            ArtifactPolicy: new ArtifactPolicy(),
            ResultPolicy: new ResultPolicy(@"C:\ProgramData\Dispatch\Runs"));
        var plan = new ExecutionPlan("run-001", DateTimeOffset.UtcNow, job, [], DryRun: true);

        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync(plan, CancellationToken.None));
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .BuildServiceProvider(validateScopes: true);
    }
}
