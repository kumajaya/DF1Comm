using System;
using System.IO.Ports;

/// <summary>
/// DF1 Full-Duplex SLC 5/03 Emulator Launcher.
/// 
/// Command line arguments:
///   <port>                   : serial port name (default COM2)
///   --baud <value>           : baud rate (default 19200)
///   --parity <none|odd|even> : parity mode (default none)
///   --node <n>               : emulator node id (default 1)
///   --checksum <crc|bcc>     : checksum mode (default crc)
///   --help, -h               : show usage
///
/// Example:
///   dotnet run --project DF1Emulator.csproj -- COM2 --baud 19200 --checksum crc
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        // Default values
        string portName = "COM2";
        int baud = 19200;
        Parity parity = Parity.None;
        int node = 1;
        string checksum = "crc";

        // Parse positional port argument
        if (args.Length > 0 && !args[0].StartsWith("--"))
            portName = args[0];

        // Parse optional arguments
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();

            if (a == "--baud" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var b))
                    baud = b;
            }
            else if (a == "--parity" && i + 1 < args.Length)
            {
                parity = args[++i].ToLowerInvariant() switch
                {
                    "odd" => Parity.Odd,
                    "even" => Parity.Even,
                    _ => Parity.None
                };
            }
            else if (a == "--node" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var n))
                    node = n;
            }
            else if (a == "--checksum" && i + 1 < args.Length)
            {
                checksum = args[++i].ToLowerInvariant();
            }
            else if (a == "--help" || a == "-h")
            {
                PrintUsage();
                return;
            }
        }

        // Create and start emulator
        using var emulator = new DF1Emulator(portName, baud, parity)
        {
            MyNode = node,
            CheckSum = checksum == "crc" ? CheckSumOptions.Crc : CheckSumOptions.Bcc
        };

        try
        {
            emulator.Start();
            Console.WriteLine($"DF1 Emulator running on {portName}");
            Console.WriteLine($"  Baud rate : {baud}");
            Console.WriteLine($"  Parity    : {parity}");
            Console.WriteLine($"  Node ID   : {node}");
            Console.WriteLine($"  Checksum  : {emulator.CheckSum}");
            Console.WriteLine("Press Enter to stop.");
            Console.ReadLine();
            emulator.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("DF1 Emulator - SLC 5/03 Full-Duplex Emulator");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [port] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --baud <n>           Baud rate (default 19200)");
        Console.WriteLine("  --parity <none|odd|even>  Parity mode (default none)");
        Console.WriteLine("  --node <n>           Emulator node ID (default 1)");
        Console.WriteLine("  --checksum <crc|bcc> Checksum mode (default crc)");
        Console.WriteLine("  --help, -h           Show this help");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  dotnet run -- COM2 --baud 19200 --checksum crc");
        Console.WriteLine("  dotnet run -- COM3 --baud 9600 --parity even --node 2");
    }
}
