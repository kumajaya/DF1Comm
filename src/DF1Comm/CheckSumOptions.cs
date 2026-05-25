namespace DF1Comm;

/// <summary>
/// Checksum selection for DF1 frames.
/// CRC-16/ARC (init=0x0000, poly=0xA001) as AB DF1 spec
/// BCC uses simple XOR (returned in low byte).
/// </summary>
public enum CheckSumOptions
{
    Crc = 0,
    Bcc = 1
}
