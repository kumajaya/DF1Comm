namespace DF1Comm;

/// <summary>
/// Checksum selection for DF1 frames.
/// CRC uses CRC-16 (Modbus polynomial 0xA001).
/// BCC uses simple XOR (returned in low byte).
/// </summary>
public enum CheckSumOptions
{
    Crc = 0,
    Bcc = 1
}
