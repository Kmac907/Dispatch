using Dispatch.Core;
using Dispatch.Transports.PsExec;

if (args.Any(static arg => arg is "--version" or "-v"))
{
    Console.WriteLine(DispatchProduct.Version);
    return 0;
}

if (args.Length == 0 || args.Any(static arg => arg is "--help" or "-h" or "/?"))
{
    Console.WriteLine($"""
Dispatch {DispatchProduct.Version}

Windows-native script orchestration for endpoint administrators.

Usage:
  dispatch [--help]
  dispatch --version

Available transports:
  {PsExecTransportDescriptor.TransportName}

Remote execution commands are not implemented in this foundation slice.
""");
    return 0;
}

Console.Error.WriteLine("Unknown arguments. Run 'dispatch --help' for usage.");
return 1;
