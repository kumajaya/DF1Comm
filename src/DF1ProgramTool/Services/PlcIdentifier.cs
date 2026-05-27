using System;
using System.Text;
using System.Threading.Tasks;
using DF1ProgramTool.Models;

namespace DF1ProgramTool.Services;

public static class PlcIdentifier
{
    public static async Task<PlcInfo> IdentifyAsync(global::DF1Comm.DF1Comm df1)
    {
        try
        {
            // Basic processor type
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

            // Defaults
            string bulletin = string.Empty;
            byte seriesRev = 0;
            byte ramKb = 0;
            string modeStr = "UNKNOWN";

            // Read diagnostic DATA[] once
            try
            {
                byte[]? data = await Task.Run(() => df1.GetDiagnosticStatusRaw());
                if (data != null && data.Length >= 16)
                {
                    // Per emulator/spec: DATA[3] = ProcessorType (redundant), DATA[5..15] = bulletin ASCII (11 bytes)
                    if (data.Length > 3)
                    {
                        // If DF1Comm GetProcessorType returned something different, keep procType from GetProcessorType()
                        // but we can override if needed:
                        // procType = data[3];
                    }

                    int bStart = 5;
                    int bLen = Math.Min(11, data.Length - bStart);
                    if (bLen > 0)
                        bulletin = Encoding.ASCII.GetString(data, bStart, bLen).Trim();

                    seriesRev = data.Length > 4 ? data[4] : (byte)0;
                    ramKb = data.Length > 22 ? data[22] : (byte)0;
                }
            }
            catch
            {
                // ignore if DF1Comm doesn't support raw diagnostic read
            }

            bool supports = family is "SLC" or "MicroLogix";
            return new PlcInfo(procType, name, supports, family, bulletin, seriesRev, ramKb, modeStr);
        }
        catch (Exception ex)
        {
            return new PlcInfo(0, $"Identify error: {ex.Message}", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");
        }
    }
}
