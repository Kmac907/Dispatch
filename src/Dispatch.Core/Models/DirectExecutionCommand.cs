namespace Dispatch.Core.Models;

public sealed record DirectExecutionCommand(
    string Executable,
    IReadOnlyList<string> Arguments)
{
    public string RenderedCommand => string.Join(
        " ",
        new[] { Executable }.Concat(Arguments.Select(QuoteIfNeeded)));

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
