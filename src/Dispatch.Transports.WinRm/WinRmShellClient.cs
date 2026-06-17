using System.Text;
using System.Xml.Linq;
using System.Runtime.Versioning;
using Dispatch.Core.Models;

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
        var requestedExecutionTimeout = request.ExecutionTimeout;
        using var executionTimeout = requestedExecutionTimeout is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (executionTimeout is not null && requestedExecutionTimeout is not null)
        {
            executionTimeout.CancelAfter(requestedExecutionTimeout.Value);
        }

        var effectiveCancellationToken = executionTimeout?.Token ?? cancellationToken;
        Dictionary<string, string>? metadata = null;

        try
        {
            var session = OpenShell(request.Target, request.ExecutionTimeout, out var shellId, out var scheme, out var port);
            try
            {
                metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scheme"] = scheme,
                    ["port"] = port.ToString(),
                    ["shellResourceUri"] = ShellResourceUri
                };
                var commandId = CreateCommand(session.Automation, session.Session, shellId, request.Executable, request.Arguments);
                SendStandardInput(session.Automation, session.Session, shellId, commandId, request.StandardInputFrames, request.CloseStandardInput);
                var receive = ReceiveUntilDone(session.Automation, session.Session, shellId, commandId, effectiveCancellationToken);

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
        catch (OperationCanceledException) when (executionTimeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata["timeoutOrigin"] = "client";
            metadata["timeoutSeconds"] = request.ExecutionTimeout?.TotalSeconds.ToString("0.###") ?? string.Empty;
            return Task.FromResult(WinRmShellCommandResult.TimedOutResult(
                $"Raw WinRM shell execution timed out for '{request.Target}' after {request.ExecutionTimeout}.",
                metadata: metadata));
        }
        catch (Exception exception) when (IsLikelyTimeout(exception))
        {
            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata["timeoutOrigin"] = "wsman-operation";
            return Task.FromResult(WinRmShellCommandResult.TimedOutResult(
                $"Raw WinRM shell operation timed out for '{request.Target}': {exception.Message}",
                metadata: metadata));
        }
        catch (Exception exception)
        {
            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failureCategory = exception is WinRmShellOpenException shellOpenException
                ? shellOpenException.FailureCategory
                : WinRmFailureClassifier.Classify(exception.Message, metadata);
            if (exception is WinRmShellOpenException detailedShellOpenException
                && detailedShellOpenException.Metadata is not null)
            {
                foreach (var pair in detailedShellOpenException.Metadata)
                {
                    metadata[pair.Key] = pair.Value;
                }
            }
            if (!metadata.ContainsKey("failureKind"))
            {
                WinRmFailureClassifier.Classify(exception.Message, metadata);
            }
            metadata["failureCategory"] = failureCategory.ToString();
            return Task.FromResult(WinRmShellCommandResult.Failed(exception.Message, metadata, failureCategory));
        }
    }

    private static OpenShellSession OpenShell(string target, TimeSpan? executionTimeout, out string shellId, out string scheme, out int port)
    {
        var failures = new List<string>();
        var failureCategories = new List<FailureCategory>();
        var failureMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

                dynamic connectionOptions = automation.CreateConnectionOptions();
                dynamic session = automation.CreateSession(attempt.ConnectionUri, flags, connectionOptions);
                session.Timeout = GetSessionTimeoutMilliseconds(executionTimeout);
                shellId = CreateShell(session, attempt.ConnectionUri);
                scheme = attempt.UseSsl ? "https" : "http";
                port = attempt.Port;
                return new OpenShellSession(automation, session);
            }
            catch (Exception exception)
            {
                failures.Add($"{attempt.ConnectionUri}: {exception.Message}");
                failureCategories.Add(WinRmFailureClassifier.Classify(exception.Message, null));
                if (exception is WinRmShellOpenException shellOpenException
                    && shellOpenException.Metadata is not null)
                {
                    foreach (var pair in shellOpenException.Metadata)
                    {
                        failureMetadata[$"{attempt.MetadataPrefix}{pair.Key}"] = pair.Value;
                    }
                }
            }
        }

        throw new WinRmShellOpenException(
            $"Could not open a raw WinRM shell for '{target}'. {string.Join(" ", failures)}",
            WinRmFailureClassifier.Choose(failureCategories),
            failureMetadata);
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

        if (WinRmShellResponseParser.TryExtractShellId(response, out var shellId, out var source))
        {
            return shellId!;
        }

        throw new WinRmShellOpenException(
            $"Raw WinRM shell creation did not return a ShellId for '{connectionUri}'.",
            FailureCategory.TransportUnavailable,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["shellCreateResponseXml"] = WinRmShellResponseParser.GetDiagnosticPayload(response),
                ["shellIdSource"] = source
            });
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
        yield return new ConnectionAttempt($"http://{target}:5985/wsman", UseSsl: false, Port: 5985, MetadataPrefix: "http5985.");
        yield return new ConnectionAttempt($"https://{target}:5986/wsman", UseSsl: true, Port: 5986, MetadataPrefix: "https5986.");
    }

    private static int GetSessionTimeoutMilliseconds(TimeSpan? executionTimeout)
    {
        if (executionTimeout is not { } timeout)
        {
            return 60000;
        }

        var milliseconds = (long)Math.Ceiling(timeout.TotalMilliseconds);
        return (int)Math.Clamp(milliseconds, 1000, int.MaxValue);
    }

    private static bool IsLikelyTimeout(Exception exception) =>
        exception.Message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
        || exception.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;

    private sealed record OpenShellSession(dynamic Automation, dynamic Session);

    private sealed record ConnectionAttempt(string ConnectionUri, bool UseSsl, int Port, string MetadataPrefix);

    private sealed record ReceiveResult(int ExitCode, string Stdout, string Stderr);

    private sealed class WinRmShellOpenException(
        string message,
        FailureCategory failureCategory,
        IReadOnlyDictionary<string, string>? metadata = null) : InvalidOperationException(message)
    {
        public FailureCategory FailureCategory { get; } = failureCategory;

        public IReadOnlyDictionary<string, string>? Metadata { get; } = metadata;
    }
}
