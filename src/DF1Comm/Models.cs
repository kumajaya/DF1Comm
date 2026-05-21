namespace DF1Comm;

/// <summary>
/// Parsed address structure used by ReadRawData/WriteRawData/ParseAddress.
/// BitNumber: 99 = no bit-level access. 0–15 = specific bit.
/// BytesPerElements: element size in bytes for the file type.
/// </summary>
public struct DataAddress
{
    public int FileNumber;
    public int FileType;
    public int Element;
    public int SubElement;
    public int BitNumber;
    public int BytesPerElements;
}

/// <summary>
/// I/O card configuration for a single rack slot.
/// Returned by GetIOConfig, GetSLCIOConfig, and GetML1500IOConfig.
/// </summary>
public struct IOConfig
{
    public int InputBytes;
    public int OutputBytes;
    public int CardCode;
}

/// <summary>
/// Data file metadata returned by GetDataMemory.
/// </summary>
public struct DataFileDetails
{
    public string FileType { get; set; }
    public int FileNumber { get; set; }
    public int NumberOfElements { get; set; }
}

/// <summary>
/// Program/ladder file container used by Upload/Download operations.
/// </summary>
public struct PLCFileDetails
{
    public int FileType { get; set; }
    public int FileNumber { get; set; }
    public int NumberOfBytes { get; set; }
    public byte[] Data { get; set; }
}
