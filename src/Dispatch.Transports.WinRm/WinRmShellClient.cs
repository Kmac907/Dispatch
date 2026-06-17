using System.Text;
using System.Xml.Linq;
using System.Runtime.Versioning;

namespace Dispatch.Transports.WinRm;

[SupportedOSPlatform("windows")]
public sealed class WinRmShellClient : IWinRmShellClient
{
    private const string ShellResourceUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";
    private const string ShellNamespace = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private const string CommandActionUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command";
    private const string ReceiveActionUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive";
    private const string SendActionUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send";
    private const string DoneCommandState = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done";

    public Task<WinRmShellCommandResult> ExecuteAsync(
        WinRmShellCommandRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var session = OpenShell(request.Target, out var shellId, out var scheme, out var port);
            try
            {
                var commandId = CreateCommand(session.Automation, session.Session, shellId, request.Executable, request.Arguments);
                SendStandardInput(session.Automation, session.Session, shellId, commandId, request.StandardInputFrames, request.CloseStandardInput);
                var receive = ReceiveUntilDone(session.Automation, session.Session, shellId, commandId, cancellationToken);
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scheme"] = scheme,
                    ["port"] = port.ToString(),
                    ["shellResourceUri"] = ShellResourceUri
                };

                return Task.FromResult(new WinRmShellCommandResult(
                    true,
                    receive.ExitCode,
                    receive.Stdout,
                    receive.Stderr,
                    null,
                    metadata));
            }
            finally
            {
                TryDeleteShell(session.Automation, session.Session, shellId);
            }
        }
        catch (Exception exception)
        {
            return Task.FromResult(WinRmShellCommandResult.Failed(exception.Message));
        }
    }

    private static OpenShellSession OpenShell(string target, out string shellId, out string scheme, out int port)
    {
        var failures = new List<string>();
        foreach (var attempt in GetConnectionAttempts(target))
        {
            try
            {
                dynamic automation = Activator.CreateInstance(
                    Type.GetTypeFromProgID("WSMan.Automation")
                    ?? throw new InvalidOperationException("WSMan.Automation COM object is not available on this machine."))
                    ?? throw new InvalidOperationException("Failed to create WSMan.Automation COM object.");
                var flags = (int)automation.SessionFlagUseNegotiate()
                            | (int)automation.SessionFlagAllowNegotiateImplicitCredentials()
                            | (int)automation.SessionFlagUTF8();
                if (attempt.UseSsl)
                {
                    flags |= (int)automation.SessionFlagUseSsl();
                }

                dynamic session = automation.CreateSession(attempt.ConnectionUri, flags, null);
                session.Timeout = 60000;
                shellId = CreateShell(session, attempt.ConnectionUri);
                scheme = attempt.UseSsl ? "https" : "http";
                port = attempt.Port;
                return new OpenShellSession(automation, session);
            }
            catch (Exception exception)
            {
                failures.Add($"{attempt.ConnectionUri}: {exception.Message}");
            }
        }

        throw new InvalidOperationException($"Could not open a raw WinRM shell for '{target}'. {string.Join(" ", failures)}");
    }

    private static string CreateShell(dynamic session, string connectionUri)
    {
        var response = (string)session.Create(
            ShellResourceUri,
            """
<rsp:Shell xmlns:rsp="http://schemas.microsoft.com/wbem/wsman/1/windows/shell">
  <rsp:InputStreams>stdin</rsp:InputStreams>
  <rsp:OutputStreams>stdout stderr</rsp:OutputStreams>
</rsp:Shell>
""",
            0);

        return GetRequiredElementValue(response, "ShellId")
               ?? throw new InvalidOperationException($"Raw WinRM shell creation did not return a ShellId for '{connectionUri}'.");
    }

    private static string CreateCommand(
        dynamic automation,
        dynamic session,
        string shellId,
        string executable,
        IReadOnlyList<string> arguments)
    {
        dynamic locator = automation.CreateResourceLocator(ShellResourceUri);
        locator.AddSelector("ShellId", shellId);

        var body = new XElement(
            XName.Get("CommandLine", ShellNamespace),
            new XElement(XName.Get("Command", ShellNamespace), executable),
            arguments.Select(argument => new XElement(XName.Get("Arguments", ShellNamespace), argument)));

        var response = (string)session.Invoke(CommandActionUri, locator, body.ToString(SaveOptions.DisableFormatting), 0);
        return GetRequiredElementValue(response, "CommandId")
               ?? throw new InvalidOperationException("Raw WinRM shell command creation did not return a CommandId.");
    }

    private static void SendStandardInput(
        dynamic automation,
        dynamic session,
        string shellId,
        string commandId,
        IReadOnlyList<byte[]> frames,
        bool closeStandardInput)
    {
        dynamic locator = automation.CreateResourceLocator(ShellResourceUri);
        locator.AddSelector("ShellId", shellId);

        foreach (var frame in frames)
        {
            var body = BuildSendBody(commandId, Convert.ToBase64String(frame), end: false);
            _ = (string)session.Invoke(SendActionUri, locator, body, 0);
        }

        if (closeStandardInput)
        {
            var body = BuildSendBody(commandId, string.Empty, end: true);
            _ = (string)session.Invoke(SendActionUri, locator, body, 0);
        }
    }

    private static ReceiveResult ReceiveUntilDone(
        dynamic automation,
        dynamic session,
        string shellId,
        string commandId,
        CancellationToken cancellationToken)
    {
        dynamic locator = automation.CreateResourceLocator(ShellResourceUri);
        locator.AddSelector("ShellId", shellId);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = $$"""
<rsp:Receive xmlns:rsp="{{ShellNamespace}}">
  <rsp:DesiredStream CommandId="{{commandId}}">stdout stderr</rsp:DesiredStream>
</rsp:Receive>
""";

            var response = (string)session.Invoke(ReceiveActionUri, locator, body, 0);
            var document = XDocument.Parse(response);
            foreach (var stream in document.Descendants().Where(static element => element.Name.LocalName == "Stream"))
            {
                var name = stream.Attribute("Name")?.Value;
                var content = stream.Value;
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(content));
                if (string.Equals(name, "stdout", StringComparison.OrdinalIgnoreCase))
                {
                    stdout.Append(decoded);
                }
                else if (string.Equals(name, "stderr", StringComparison.OrdinalIgnoreCase))
                {
                    stderr.Append(decoded);
                }
            }

            var commandState = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "CommandState");
            if (commandState?.Attribute("State")?.Value == DoneCommandState)
            {
                var exitCodeValue = commandState.Descendants().FirstOrDefault(static element => element.Name.LocalName == "ExitCode")?.Value;
                var exitCode = int.TryParse(exitCodeValue, out var parsedExitCode)
                    ? parsedExitCode
                    : 0;
                return new ReceiveResult(exitCode, stdout.ToString(), stderr.ToString());
            }
        }
    }

    private static void TryDeleteShell(dynamic automation, dynamic session, string shellId)
    {
        try
        {
            dynamic locator = automation.CreateResourceLocator(ShellResourceUri);
            locator.AddSelector("ShellId", shellId);
            session.Delete(locator, 0);
        }
        catch
        {
        }
    }

    private static string BuildSendBody(string commandId, string content, bool end)
    {
        var endAttribute = end ? " End=\"true\"" : string.Empty;
        return $"""
<rsp:Send xmlns:rsp="{ShellNamespace}">
  <rsp:Stream Name="stdin" CommandId="{commandId}"{endAttribute}>{content}</rsp:Stream>
</rsp:Send>
""";
    }

    private static string? GetRequiredElementValue(string xml, string localName) =>
        XDocument.Parse(xml)
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)
            ?.Value;

    private static IEnumerable<ConnectionAttempt> GetConnectionAttempts(string target)
    {
        yield return new ConnectionAttempt($"http://{target}:5985/wsman", UseSsl: false, Port: 5985);
        yield return new ConnectionAttempt($"https://{target}:5986/wsman", UseSsl: true, Port: 5986);
    }

    private sealed record OpenShellSession(dynamic Automation, dynamic Session);

    private sealed record ConnectionAttempt(string ConnectionUri, bool UseSsl, int Port);

    private sealed record ReceiveResult(int ExitCode, string Stdout, string Stderr);
}
