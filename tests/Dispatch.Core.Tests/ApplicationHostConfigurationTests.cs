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

        Assert.Null(options.Inventory);
        Assert.Null(options.Target);
        Assert.Null(options.Exclude);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.LocalRunRoot);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(8, options.Throttle);
        Assert.Equal([0], options.ExpectedExitCodes);
        Assert.Equal("psexec.exe", options.PsExecPath);
        Assert.NotNull(provider.GetRequiredService<IDispatchPlanner>());
        Assert.NotNull(provider.GetRequiredService<IDispatchExecutor>());
    }

    [Fact]
    public void CoreOptionsBindFromConfiguration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Dispatch:Inventory"] = "hosts\\prod.yml",
            ["Dispatch:Target"] = "web",
            ["Dispatch:Exclude"] = "tag:canary",
            ["Dispatch:LocalRunRoot"] = "D:\\Dispatch\\Runs",
            ["Dispatch:RemoteRunRoot"] = "D:\\RemoteDispatch",
            ["Dispatch:DefaultTransport"] = "PsExec",
            ["Dispatch:Throttle"] = "16",
            ["Dispatch:PsExecPath"] = "C:\\Tools\\PsExec.exe",
            ["Dispatch:ExpectedExitCodes:0"] = "0",
            ["Dispatch:ExpectedExitCodes:1"] = "3010"
        });

        var options = provider.GetRequiredService<IOptions<DispatchOptions>>().Value;

        Assert.Equal("hosts\\prod.yml", options.Inventory);
        Assert.Equal("web", options.Target);
        Assert.Equal("tag:canary", options.Exclude);
        Assert.Equal("D:\\Dispatch\\Runs", options.LocalRunRoot);
        Assert.Equal("D:\\RemoteDispatch", options.RemoteRunRoot);
        Assert.Equal(TransportKind.PsExec, options.DefaultTransport);
        Assert.Equal(16, options.Throttle);
        Assert.Equal("C:\\Tools\\PsExec.exe", options.PsExecPath);
        Assert.Equal([0, 3010], options.ExpectedExitCodes);
    }

    [Fact]
    public async Task PlannerAndExecutorAreRegistered()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        using var script = TemporaryScript.Create();

        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.NotEmpty(plan.RunId);
        Assert.Single(plan.Targets);

        var result = await executor.ExecuteAsync(plan, CancellationToken.None);
        var target = Assert.Single(result.Targets);

        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.TransportUnavailable, target.FailureCategory);
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
