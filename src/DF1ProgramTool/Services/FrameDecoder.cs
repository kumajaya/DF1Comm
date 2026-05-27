using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace DF1ProgramTool.Services;

public static class FrameDecoder
{
    /// <summary>
    /// Remove DLE stuffing: 0x10 0x10 → single 0x10.
    /// </summary>
    private static byte[] RemoveDleStuffing(byte[] stuffed)
    {
        var list = new List<byte>(stuffed.Length);
        for (int i = 0; i < stuffed.Length; i++)
        {
            byte b = stuffed[i];
            if (b == 0x10 && i + 1 < stuffed.Length && stuffed[i + 1] == 0x10)
            {
                list.Add(0x10);
                i++;
            }
            else
                list.Add(b);
        }
        return list.ToArray();
    }

    public static string Decode(byte[] raw)
    {
        if (raw.Length == 2 && raw[0] == 0x10)
            return raw[1] == 0x06 ? "ACK" : raw[1] == 0x15 ? "NAK" : raw[1] == 0x05 ? "ENQ" : $"DLE 0x{raw[1]:X2}";

        if (raw.Length < 6 || raw[0] != 0x10 || raw[1] != 0x02)
            return $"Invalid: {Hex(raw)}";

        // Locate DLE ETX (skip stuffed DLE DLE)
        int etx = -1;
        for (int i = 2; i < raw.Length - 1; i++)
            if (raw[i] == 0x10 && raw[i+1] == 0x10) i++;
            else if (raw[i] == 0x10 && raw[i+1] == 0x03) { etx = i; break; }
        if (etx == -1) return "No ETX";

        byte[] unstuffed = RemoveDleStuffing(raw.Skip(2).Take(etx - 2).ToArray());
        if (unstuffed.Length < 6) return "Payload too short";

        int dst = unstuffed[0], src = unstuffed[1], cmd = unstuffed[2], sts = unstuffed[3];
        int tns = unstuffed[4] | (unstuffed[5] << 8);
        int fnc = unstuffed.Length >= 7 ? unstuffed[6] : 0;
        byte[] data = unstuffed.Length > 7 ? unstuffed.Skip(7).ToArray() : Array.Empty<byte>();

        var sb = new StringBuilder();
        sb.AppendLine($"DST={dst} SRC={src} TNS={tns} CMD=0x{cmd:X2} FNC=0x{fnc:X2} STS={sts}");

        if (cmd == 0x0F && (fnc == 0xA1 || fnc == 0xA2 || fnc == 0xAA || fnc == 0xAB) && data.Length >= 3)
        {
            int size = data[0], fileNum = data[1], fileType = data[2];
            string typeStr = fileType switch
            {
                0x84 => "Status", 0x85 => "Binary", 0x86 => "Timer", 0x87 => "Counter",
                0x88 => "Control", 0x89 => "Integer", 0x8A => "Float", 0x8B => "Output",
                0x8C => "Input", 0x8D => "String", _ => $"0x{fileType:X2}"
            };
            int elem = data[3], idx = 4;
            if (elem == 0xFF && data.Length >= idx+2) { elem = data[idx] | (data[idx+1] << 8); idx += 2; }
            sb.AppendLine($"              Size={size}, File={fileNum}, Type={typeStr}, Element={elem}");
            if ((fnc == 0xA2 || fnc == 0xAB) && data.Length > idx)
                sb.AppendLine($"              SubElem={data[idx]}");
            if (fnc == 0xAB && data.Length >= idx+4)
                sb.AppendLine($"              Mask=0x{(data[idx] | (data[idx+1] << 8)):X4}");
        }
        else if (cmd == 0x06 && fnc == 0x03)
            sb.AppendLine("              (Diagnostic status data)");

        return sb.ToString().TrimEnd();
    }

    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');
}
