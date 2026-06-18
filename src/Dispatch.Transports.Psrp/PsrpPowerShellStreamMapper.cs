using Dispatch.Core.Models;
using System.Management.Automation;

namespace Dispatch.Transports.Psrp;

internal static class PsrpPowerShellStreamMapper
{
    public static IReadOnlyList<PowerShellStreamRecord>? Capture(PSDataStreams streams)
    {
        List<PowerShellStreamRecord>? records = null;

        AddRecords(streams.Error, PowerShellStreamKind.Error, static record => record.ToString(), ref records);
        AddRecords(streams.Warning, PowerShellStreamKind.Warning, static record => record.Message, ref records);
        AddRecords(streams.Verbose, PowerShellStreamKind.Verbose, static record => record.Message, ref records);
        AddRecords(streams.Debug, PowerShellStreamKind.Debug, static record => record.Message, ref records);
        AddInformationRecords(streams.Information, ref records);

        return records is { Count: > 0 } ? records : null;
    }

    public static string GetErrorText(IReadOnlyList<PowerShellStreamRecord>? streams) =>
        streams is null
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                streams
                    .Where(static stream => stream.Stream == PowerShellStreamKind.Error)
                    .Select(static stream => stream.Message)
                    .Where(static message => !string.IsNullOrWhiteSpace(message)));

    private static void AddInformationRecords(
        PSDataCollection<InformationRecord> stream,
        ref List<PowerShellStreamRecord>? records)
    {
        foreach (var record in stream)
        {
            var message = record.MessageData?.ToString();
            if (string.IsNullOrWhiteSpace(message))
            {
                message = record.ToString();
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            records ??= [];
            records.Add(new PowerShellStreamRecord(PowerShellStreamKind.Information, message));
        }
    }

    private static void AddRecords<TRecord>(
        IEnumerable<TRecord> stream,
        PowerShellStreamKind kind,
        Func<TRecord, string?> selector,
        ref List<PowerShellStreamRecord>? records)
    {
        foreach (var record in stream)
        {
            var message = selector(record);
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            records ??= [];
            records.Add(new PowerShellStreamRecord(kind, message));
        }
    }
}
