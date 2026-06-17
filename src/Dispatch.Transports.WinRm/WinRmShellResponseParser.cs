using System.Xml.Linq;

namespace Dispatch.Transports.WinRm;

internal static class WinRmShellResponseParser
{
    public static bool TryExtractShellId(
        string responseXml,
        out string? shellId,
        out string source)
    {
        shellId = null;
        source = "missing";

        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return false;
        }

        var document = XDocument.Parse(responseXml);

        var shellIdElement = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "ShellId");
        var shellIdValue = Normalize(shellIdElement?.Value);
        if (shellIdValue is not null)
        {
            shellId = shellIdValue;
            source = "element";
            return true;
        }

        var selectorElement = document
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName == "Selector"
                && string.Equals(
                    element.Attributes().FirstOrDefault(static attribute => attribute.Name.LocalName == "Name")?.Value,
                    "ShellId",
                    StringComparison.OrdinalIgnoreCase));
        var selectorValue = Normalize(selectorElement?.Value);
        if (selectorValue is not null)
        {
            shellId = selectorValue;
            source = "selector";
            return true;
        }

        return false;
    }

    public static string GetDiagnosticPayload(string? responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return "<empty>";
        }

        return responseXml;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
