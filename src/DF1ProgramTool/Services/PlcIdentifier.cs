using System;
using System.Threading.Tasks;
using DF1ProgramTool.Models;

namespace DF1ProgramTool.Services;

public static class PlcIdentifier
{
    public static async Task<PlcInfo> IdentifyAsync(global::DF1Comm.DF1Comm df1)
    {
        try
        {
            int procType = await Task.Run(() => df1.GetProcessorType());

            (string name, string family) = procType switch
            {
                0x49 => ("SLC 5/03",        "SLC"),
                0x5B => ("SLC 5/04",        "SLC"),
                0x88 => ("SLC 5/01",        "SLC"),
                0x89 => ("SLC 5/02",        "SLC"),
                0x8C => ("MicroLogix 1500", "MicroLogix"),
                0x9C => ("SLC 5/05",        "SLC"),
                0x58 => ("MicroLogix 1000", "MicroLogix"),
                0x0B or 0x0E => ("PLC-5",   "PLC5"),
                _    => ($"Unknown (0x{procType:X2})", "Unknown")
            };

            // Upload/download only supported for SLC and MicroLogix families
            bool supports = family is "SLC" or "MicroLogix";

            return new PlcInfo(procType, name, supports, family);
        }
        catch (Exception ex)
        {
            return new PlcInfo(0, $"Identify error: {ex.Message}", false, "Unknown");
        }
    }
}
