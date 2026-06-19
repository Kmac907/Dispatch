using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dispatch.Core.Hosting;

public static class DispatchServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchCore(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<DispatchOptions>()
            .Bind(configuration.GetSection(DispatchOptions.SectionName))
            .PostConfigure(options =>
            {
                if (options.ExpectedExitCodes.Length == 0)
                {
                    options.ExpectedExitCodes = [0];
                }
            })
            .Validate(options => !string.IsNullOrWhiteSpace(options.LocalRunRoot), "Local run root is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RemoteRunRoot), "Remote run root is required.")
            .Validate(options => options.Throttle > 0, "Throttle must be greater than zero.")
            .Validate(options => options.ExpectedExitCodes.Length > 0, "At least one expected exit code is required.");

        services.AddSingleton<IRunIdGenerator, DispatchRunIdGenerator>();
        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<ILocalRunLayoutService, LocalRunLayoutService>();
        services.AddSingleton<IEndpointFileSystem, EndpointFileSystem>();
        services.AddSingleton<IScriptPreparationService, ScriptPreparationService>();
        services.AddSingleton<IDispatchArtifactCollector, DispatchArtifactCollector>();
        services.AddSingleton<IDispatchResultWriter, DispatchResultWriter>();
        services.AddSingleton<IDispatchPlanner, DispatchPlanner>();
        services.AddSingleton<IDispatchExecutor, DispatchExecutor>();
        services.AddSingleton<ICredentialProvider>(services =>
        {
            var options = services.GetRequiredService<IOptions<DispatchOptions>>();
            if (configuration.GetSection("Credentials").GetChildren().Any())
            {
                return new ConfigurationCredentialProvider(configuration, options);
            }

            var providerName = options.Value.CredentialProvider;
            return string.Equals(providerName, "file", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(providerName, "local", StringComparison.OrdinalIgnoreCase)
                ? new FileCredentialProvider(options)
                : new UnavailableCredentialProvider(options);
        });

        return services;
    }
}
