using Dispatch.Core.Credentials;
using System.Security;

namespace Dispatch.Cli;

internal sealed class ConsoleRuntimeCredentialPrompt : IRuntimeCredentialPrompt
{
    private static readonly object SyncRoot = new();

    public Task<SecureString> PromptForPasswordAsync(
        RuntimeCredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                $"Credential '{request.ReferenceName}' requires an interactive secure password prompt, but console input is redirected.");
        }

        lock (SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Console.Error.WriteLine($"Credential: {request.ReferenceName}");
            Console.Error.WriteLine($"Username: {request.UserName}");
            Console.Error.Write($"{request.PromptLabel}: ");

            var password = new SecureString();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    return Task.FromResult(password);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.RemoveAt(password.Length - 1);
                    }

                    continue;
                }

                if (key.KeyChar == '\0')
                {
                    continue;
                }

                password.AppendChar(key.KeyChar);
            }
        }
    }
}
