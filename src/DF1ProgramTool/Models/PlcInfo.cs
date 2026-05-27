using System;

namespace DF1ProgramTool.Models;

public record PlcInfo(
    int ProcessorType,
    string Name,
    bool SupportsUploadDownload,
    string Family,    // "SLC", "MicroLogix", "PLC5", "Unknown"
    string Bulletin,  // e.g. "5/03"
    byte SeriesRevision,
    byte RamKb,
    string ModeStr)   // e.g. "RUN" or "PROG"
{
    /// <summary>
    /// Generates a default filename using the actual run mode string.
    /// </summary>
    /// <param name="modeStr">e.g. "RUN" or "PROG"</param>
    public string GetDefaultFileName(string modeStr = "UNKNOWN")
    {
        string familyTag = Family switch
        {
            "SLC"        => "SLC",
            "MicroLogix" => "ML",
            "PLC5"       => "PLC5",
            _            => "PLC"
        };
        string bulletinTag = string.IsNullOrWhiteSpace(Bulletin) ? "unknown" : Bulletin.Replace(' ', '_');
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        return $"{familyTag}_{ProcessorType:X2}_{bulletinTag}_{modeStr}_{timestamp}.bin";
    }
}
