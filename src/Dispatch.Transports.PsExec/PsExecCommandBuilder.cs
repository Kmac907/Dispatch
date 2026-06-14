using Dispatch.Core.Configuration;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Microsoft.Extensions.Options;

namespace Dispatch.Transports.PsExec;

public sealed class PsExecCommandBuilder(IOptions<DispatchOptions> options)
{
    public PsExecCommand Build(TransportScriptExecutionRequest request)
    {
        if (request.Target.PlannedCommand is null)
        {
            throw new InvalidOperationException($"Target '{request.Target.Target.Name}' does not have a planned command.");
        }

        var executable = string.IsNullOrWhiteSpace(options.Value.PsExecPath)
            ? "psexec.exe"
            : options.Value.PsExecPath;

        var arguments = new List<string>
        {
            $@"\\{request.Target.Target.Name}"
        };

        if (request.Plan.Job.ExecutionContext.RunAsSystem)
        {
            arguments.Add("-s");
            arguments.Add("-h");
        }

        arguments.Add(request.Target.PlannedCommand.Executable);
        arguments.AddRange(request.Target.PlannedCommand.Arguments);

        return new PsExecCommand(executable, arguments, Render(executable, arguments));
    }

    private static string Render(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteIfNeeded(executable) }.Concat(arguments.Select(QuoteIfNeeded)));

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
