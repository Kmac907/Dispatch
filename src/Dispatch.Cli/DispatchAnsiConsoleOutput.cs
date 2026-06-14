using Spectre.Console;
using System.Text;

namespace Dispatch.Cli;

internal sealed class DispatchAnsiConsoleOutput(TextWriter writer, bool isTerminal) : IAnsiConsoleOutput
{
    public TextWriter Writer { get; } = writer;

    public bool IsTerminal { get; } = isTerminal;

    public int Width => TryGetConsoleValue(static () => Console.WindowWidth, 120);

    public int Height => TryGetConsoleValue(static () => Console.WindowHeight, 40);

    public void SetEncoding(Encoding encoding)
    {
        if (Writer != Console.Out && Writer != Console.Error)
        {
            return;
        }

        try
        {
            Console.OutputEncoding = encoding;
        }
        catch (IOException)
        {
        }
    }

    private static int TryGetConsoleValue(Func<int> valueFactory, int defaultValue)
    {
        try
        {
            var value = valueFactory();
            return value > 0 ? value : defaultValue;
        }
        catch (IOException)
        {
            return defaultValue;
        }
    }
}
