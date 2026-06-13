using Dispatch.Cli;
using Microsoft.Extensions.DependencyInjection;

using var host = DispatchCliHost.Build(args);
var application = host.Services.GetRequiredService<DispatchCliApplication>();
return await application.RunAsync(args, CancellationToken.None);
