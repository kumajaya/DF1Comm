using System;
using System.Collections.Generic;

/// <summary>
/// In-memory PLC file store simulating an SLC 5/03.
/// File directory layout follows AB Publication 1770-6.5.16.
///
///   offset 46/47 = number of program files (little-endian)
///   offset 52/53 = number of data tables   (little-endian)
///   offset 70/71 = total directory size in bytes (little-endian)
///                  ← DF1Comm reads this via element 0x23 (35) * bpe(2) = byte offset 70
///   offset 79    = file table, 10 bytes per entry: [fileType, sizeLo, sizeHi, fileNumber, ...]
///
/// ReadRaw: the `element` parameter is a raw byte offset into the file array.
///          DF1Comm computes this as: element * bytesPerElement + subElement * 2.
///
/// SLC 5/03 constraints (based on typical memory map):
///   - Total user memory : 14K words maximum
///   - I/O capacity      : depends on chassis size (here we emulate a moderate setup)
///   - Status file       : S:0 – S:83 (84 words, but we only allocate 83 words 0‑based)
///   - Supports B, N, T, C, R, F, and additional B/N files beyond the basic set
///
/// Data table file numbers:
///   0 = O0  (Output,  type 0x8B, 2 elems, 6 words  = 12 bytes) ← real PLC: Elems=2, Words=6
///   1 = I1  (Input,   type 0x8C, 7 elems, 21 words = 42 bytes) ← real PLC: Elems=7, Words=21
///   2 = S2  (Status,  type 0x84, 84 words  = 168 bytes, S:0–S:83)
///   3 = B3  (Binary,  type 0x85, 14 words  = 28 bytes)
///   4 = T4  (Timer,   type 0x86, 78 elem   = 468 bytes, 6 bytes/elem)
///   5 = C5  (Counter, type 0x87, 1 elem    = 6 bytes)
///   6 = R6  (Control, type 0x88, 2 elem    = 12 bytes)
///   7 = N7  (Integer, type 0x89, 74 words  = 148 bytes)
///   8 = F8  (Float,   type 0x8A, 38 elem   = 152 bytes, 4 bytes/elem)
///   9 = B9  (Binary,  type 0x85, 10 words  = 20 bytes)
///  10 = B10 (Binary,  type 0x85, 71 words  = 142 bytes)
///  11 = B11 (Binary,  type 0x85, 9 words   = 18 bytes)
///  12 = B12 (Binary,  type 0x85, 1 word    = 2 bytes)
///  13 = B13 (Binary,  type 0x85, 2 words   = 4 bytes)
///  14 = B14 (Binary,  type 0x85, 1 word    = 2 bytes)
///  15 = B15 (Binary,  type 0x85, 41 words  = 82 bytes)
///  16 = B16 (Binary,  type 0x85, 41 words  = 82 bytes)
///  17 = N17 (Integer, type 0x89, 26 words  = 52 bytes)
///  29 = B29 (Binary,  type 0x85, 26 words  = 52 bytes)
///  30 = B30 (Binary,  type 0x85, 26 words  = 52 bytes)
///  31 = B31 (Binary,  type 0x85, 26 words  = 52 bytes)
/// </summary>
public class PlcMemory
{
    // Key: (fileType, fileNumber) → raw byte array
    private readonly Dictionary<(int, int), byte[]> _files = new();
    // Bytes-per-element metadata used by Write to convert element+subElement to byte offset
    private readonly Dictionary<(int, int), int> _bytesPerElement = new();
    private readonly object _lock = new object();
    
    // Fast lookup: file number → file type code (0 if not a data file)
    private readonly Dictionary<int, int> _fileTypeByNumber = new();

    public PlcMemory()
    {
        // ── File directory (fileType=1, fileNumber=0) ─────────────────────────
        // DF1Comm ReadFileDirectory (default processor path):
        //   Step 1: reads 2 bytes at element 0x23 (35) → byte offset 35*2 = 70 → directory size
        //   Step 2: reads <size> bytes at element 0   → byte offset 0     → full directory
        //
        // Inside the full directory (byte-indexed directly by DF1Comm):
        //   [46..47] = number of program files
        //   [52..53] = number of data tables
        //   [79..]   = file table, 10 bytes per entry
        //
        const int dirSize = 659; // 79 header + 58 entries × 10 bytes
                                // = 32 data + 2 SYS (file 0,1) + 24 LAD (file 2-25)
        var file0 = new byte[dirSize];
        WriteU16(file0, 70, dirSize);
        WriteU16(file0, 46, 26);       // 26 program files: SYS×2 + LAD×24
        WriteU16(file0, 52, 32);       // 32 data file slots (21 active + 11 inactive)

        // File table starts at offset 79
        int pos = 79;
        int addr = 0;   // current base address in WORDS (data table memory)

        // Local helper to write a 10-byte data file entry.
        // Per AB specification for SLC 500, the size field is stored in WORDS, not bytes.
        void Reg(byte[] buf, int offset, byte fileType, int sizeBytes, byte fileNumber, int elemSize = 2)
        {
            int sizeWords = sizeBytes / 2;   // convert to words (critical for correct interpretation)

            buf[offset]     = fileType;
            buf[offset + 1] = (byte)(sizeWords & 0xFF);
            buf[offset + 2] = (byte)(sizeWords >> 8);
            buf[offset + 3] = fileNumber;
            buf[offset + 4] = 0x00;                       // attribute: normal
            buf[offset + 5] = (byte)elemSize;             // element size in bytes
            buf[offset + 6] = (byte)(addr & 0xFF);        // base address low word
            buf[offset + 7] = (byte)((addr >> 8) & 0xFF); // base address high word
            buf[offset + 8] = 0x00;                       // reserved
            buf[offset + 9] = 0x00;                       // reserved

            addr += sizeWords;    // advance base address by number of words
            _fileTypeByNumber[fileNumber] = fileType;   // store for fast lookup
        }

        // ========== DATA FILES (file numbers 0..31) ==========
        // Write all data files first, in order of file number.
        Reg(file0, pos, 0x8B, 12, 0, 2); pos += 10;  // O0 — 2 elems, 6 words = 12 bytes
        Reg(file0, pos, 0x8C, 42, 1, 2); pos += 10;  // I1 — 7 elems, 21 words = 42 bytes
        Reg(file0, pos, 0x84, 168, 2, 2); pos += 10; // S2
        Reg(file0, pos, 0x85, 28, 3, 2); pos += 10;  // B3
        Reg(file0, pos, 0x86, 468, 4, 6); pos += 10; // T4
        Reg(file0, pos, 0x87, 6, 5, 6); pos += 10;   // C5
        Reg(file0, pos, 0x88, 12, 6, 6); pos += 10;  // R6
        Reg(file0, pos, 0x89, 148, 7, 2); pos += 10; // N7
        Reg(file0, pos, 0x8A, 152, 8, 4); pos += 10; // F8
        Reg(file0, pos, 0x85, 20, 9, 2); pos += 10;  // B9
        Reg(file0, pos, 0x85, 142, 10, 2); pos += 10; // B10
        Reg(file0, pos, 0x85, 18, 11, 2); pos += 10; // B11
        Reg(file0, pos, 0x85, 2, 12, 2); pos += 10;  // B12
        Reg(file0, pos, 0x85, 4, 13, 2); pos += 10;  // B13
        Reg(file0, pos, 0x85, 2, 14, 2); pos += 10;  // B14
        Reg(file0, pos, 0x85, 82, 15, 2); pos += 10; // B15
        Reg(file0, pos, 0x85, 82, 16, 2); pos += 10; // B16
        Reg(file0, pos, 0x89, 52, 17, 2); pos += 10; // N17

        // File numbers 18..28 — inactive (allocated slots, no data)
        // Real PLC shows Total Files=32 but Active Files=21; files 18-28 exist as
        // empty directory entries (fileType=0x00, size=0) occupying allocated slots.
        for (int n = 18; n <= 28; n++)
        {
            file0[pos]     = 0x00; // fileType = 0 (unused/inactive)
            file0[pos + 1] = 0x00; // size lo = 0
            file0[pos + 2] = 0x00; // size hi = 0
            file0[pos + 3] = (byte)n; // file number
            file0[pos + 4] = 0x00; // attribute
            file0[pos + 5] = 0x00; // element size
            file0[pos + 6] = 0x00; // base address lo
            file0[pos + 7] = 0x00; // base address hi
            file0[pos + 8] = 0x00;
            file0[pos + 9] = 0x00;
            pos += 10;
        }

        // File numbers 29..31
        Reg(file0, pos, 0x85, 52, 29, 2); pos += 10;
        Reg(file0, pos, 0x85, 52, 30, 2); pos += 10;
        Reg(file0, pos, 0x85, 52, 31, 2); pos += 10;

        // ========== PROGRAM FILES ==========
        // File 0  = SYS (always present, not shown in RSLinx list)
        // File 1  = SYS (shown in RSLinx list)
        // Active LAD: 2, 3, 5, 8, 12, 15, 18, 19, 22, 23
        // Inactive  : 4, 6, 7, 9-11, 13-14, 16-17, 20-21, 24-25

        // File 0: SYS
        file0[pos]     = 0x01; // file type: SYS
        file0[pos + 1] = 0x01; // size lo = 1 word
        file0[pos + 2] = 0x00; // size hi
        file0[pos + 3] = 0x00; // file number = 0
        file0[pos + 4] = 0x00; // attribute
        file0[pos + 5] = 0x00; // element size
        for (int k = 6; k < 10; k++) file0[pos + k] = 0;
        pos += 10;

        // File 1: SYS
        file0[pos]     = 0x01; // file type: SYS
        file0[pos + 1] = 0x01; // size lo = 1 word
        file0[pos + 2] = 0x00; // size hi
        file0[pos + 3] = 0x01; // file number = 1
        file0[pos + 4] = 0x00; // attribute
        file0[pos + 5] = 0x00; // element size
        for (int k = 6; k < 10; k++) file0[pos + k] = 0;
        pos += 10;

        // LAD files 2..25
        int[] activeLad = { 2, 3, 5, 8, 12, 15, 18, 19, 22, 23 };
        for (int n = 2; n <= 25; n++)
        {
            bool active = Array.IndexOf(activeLad, n) >= 0;
            file0[pos]     = active ? (byte)0x02 : (byte)0x00; // LAD or inactive
            file0[pos + 1] = active ? (byte)0x01 : (byte)0x00; // size = 1 word if active
            file0[pos + 2] = 0x00;
            file0[pos + 3] = (byte)n;
            file0[pos + 4] = 0x00;
            file0[pos + 5] = 0x00;
            for (int k = 6; k < 10; k++) file0[pos + k] = 0;
            pos += 10;
        }

        // Store the directory file itself
        _files[(1, 0)] = file0;

        // ── O0 — Output (file 0, 2 elems, 6 words = 12 bytes) ──────────────────
        // Real PLC: Elems=2, Words=6. Each element = 3 words (6 bytes).
        _files[(0x8B, 0)] = new byte[12];
        _bytesPerElement[(0x8B, 0)] = 2;
        WriteU16(_files[(0x8B, 0)], 0, 0x0201);
        WriteU16(_files[(0x8B, 0)], 2, 0x0403);
        WriteU16(_files[(0x8B, 0)], 4, 0x0605);
        WriteU16(_files[(0x8B, 0)], 6, 0x0807);
        WriteU16(_files[(0x8B, 0)], 8, 0x0A09);
        WriteU16(_files[(0x8B, 0)], 10, 0x0C0B);

        // ── I1 — Input (file 1, 7 elems, 21 words = 42 bytes) ──────────────────
        // Real PLC: Elems=7, Words=21. Each element = 3 words (6 bytes).
        _files[(0x8C, 1)] = new byte[42];
        _bytesPerElement[(0x8C, 1)] = 2;

        // ── S2 — Status (file 2, 84 words = S:0–S:83) ────────────────────────
        // Values initialised from the supplied hex dump (little-endian words).
        _files[(0x84, 2)] = new byte[168];
        _bytesPerElement[(0x84, 2)] = 2;

        ushort[] s2vals = new ushort[]
        {
            // S2:0 .. S2:9
            0x0004, 0x001E, 0x9012, 0xA003, 0x69C4, 0x0000, 0x0000, 0x0000, 0x0000, 0x0003,
            // S2:10 .. S2:19
            0x0000, 0x0000, 0x0000, 0x0000, 0x001E, 0x0401, 0x0016, 0x0002, 0x0000, 0x0000,
            // S2:20 .. S2:29
            0x0000, 0x0000, 0x0031, 0x000C, 0x0020, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:30 .. S2:39
            0x0000, 0x0000, 0x0018, 0x0000, 0x007D, 0x0000, 0x07EA, 0x0005, 0x0015, 0x0000,
            // S2:40 .. S2:49
            0x0000, 0x0034, 0x002E, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:50 .. S2:59
            0x0000, 0x0000, 0x0000, 0x0004, 0x0000, 0x0000, 0x012E, 0x012E, 0x0004, 0x0000,
            // S2:60 .. S2:69
            0x0214, 0x0004, 0x0008, 0x0001, 0x005F, 0x0010, 0x01E0, 0x0006, 0x0000, 0x0000,
            // S2:70 .. S2:79
            0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:80 .. S2:83
            0x0000, 0x0000, 0x0000, 0x0000
        };
        for (int i = 0; i < s2vals.Length; i++)
            WriteU16(_files[(0x84, 2)], i * 2, s2vals[i]);

        // ── B3 — Binary (file 3, 14 words) ──────────────────────────────────
        _files[(0x85, 3)] = new byte[28];
        _bytesPerElement[(0x85, 3)] = 2;
        WriteU16(_files[(0x85, 3)], 0, 0xAA55);
        WriteU16(_files[(0x85, 3)], 2, 0x0FF0);

        // ── T4 — Timer (file 4, 78 elements × 6 bytes) ──────────────────────
        _files[(0x86, 4)] = new byte[468];
        _bytesPerElement[(0x86, 4)] = 6;

        // ── C5 — Counter (file 5, 1 element × 6 bytes) ──────────────────────
        _files[(0x87, 5)] = new byte[6];
        _bytesPerElement[(0x87, 5)] = 6;

        // ── R6 — Control (file 6, 2 elements × 6 bytes) ─────────────────────
        _files[(0x88, 6)] = new byte[12];
        _bytesPerElement[(0x88, 6)] = 6;

        // ── N7 — Integer (file 7, 74 words) ─────────────────────────────────
        _files[(0x89, 7)] = new byte[148];
        _bytesPerElement[(0x89, 7)] = 2;
        WriteU16(_files[(0x89, 7)], 0, 123);
        WriteU16(_files[(0x89, 7)], 2, 456);
        WriteU16(_files[(0x89, 7)], 4, -789);

        // ── F8 — Float (file 8, 38 elements × 4 bytes) ──────────────────────
        _files[(0x8A, 8)] = new byte[152];
        _bytesPerElement[(0x8A, 8)] = 4;
        Array.Copy(BitConverter.GetBytes(1.23f), 0, _files[(0x8A, 8)], 0, 4);
        Array.Copy(BitConverter.GetBytes(4.56f), 0, _files[(0x8A, 8)], 4, 4);

        // ── Additional B and N files (9..17, 29..31) ────────────────────────
        CreateDataFile(0x85, 9, 20, 2);   // B9
        CreateDataFile(0x85, 10, 142, 2); // B10
        CreateDataFile(0x85, 11, 18, 2);  // B11
        CreateDataFile(0x85, 12, 2, 2);   // B12
        CreateDataFile(0x85, 13, 4, 2);   // B13
        CreateDataFile(0x85, 14, 2, 2);   // B14
        CreateDataFile(0x85, 15, 82, 2);  // B15
        CreateDataFile(0x85, 16, 82, 2);  // B16
        CreateDataFile(0x89, 17, 52, 2);  // N17
        CreateDataFile(0x85, 29, 52, 2);  // B29
        CreateDataFile(0x85, 30, 52, 2);  // B30
        CreateDataFile(0x85, 31, 52, 2);  // B31

// ── I/O Configuration file (file type 0x60, file number 0) ──────────────────
        // Source: AB Publication 1747-UM011, S:96 area; accessed via CMD=0x0F FNC=0xA2.
        // DF1Comm.GetSlotCount reads 4 bytes at element=0; byte [0] is the raw chassis
        // slot count. GetSlotCount() returns (raw - 1).
        // DF1Comm.GetSLCIOConfig reads (4 + slots*6 + 8) bytes at element=0.
        // Each slot i occupies 6 bytes at file offset (i*6 + 4):
        //   +0 = InputBytes, +2 = OutputBytes, +4..5 = CardCode (little-endian)
        //
        // Chassis: 1746-A7 7-slot rack, 1 rack installed (Rack 1).
        //   Slot 0: 1747-L532E — CPU, no I/O image
        //   Slot 1: 1746-IB16  — 16-ch digital input  (SINK 24VDC),  2 InputBytes
        //   Slot 2: 1746-IB16  — 16-ch digital input  (SINK 24VDC),  2 InputBytes
        //   Slot 3: 1746-IB16  — 16-ch digital input  (SINK 24VDC),  2 InputBytes
        //   Slot 4: 1746-OB16  — 16-ch digital output (TRANS-SRC),   2 OutputBytes
        //   Slot 5: 1746-OB16  — 16-ch digital output (TRANS-SRC),   2 OutputBytes
        //   Slot 6: 1746-NI4   — 4-ch analog input,   8 InputBytes + 2 OutputBytes
        //
        // Raw slot count = 8 → GetSlotCount() returns 7 → IOConfig[8] (i = 0..7).
        // Minimum buffer = 4 + 8*6 + 2 = 54 bytes; padded to 64.
        CreateDataFile(0x60, 0, 64, 2);
        byte[] ioConfig = _files[(0x60, 0)];
        ioConfig[0] = 8;                            // raw slot count (DF1Comm subtracts 1)

        // Slot base offset = i * 6 + 4; +0 = InputBytes, +2 = OutputBytes

        // Slot 1 — 1746-IB16, 16-ch digital input
        ioConfig[1 * 6 + 4 + 0] = 2;               // InputBytes = 2

        // Slot 2 — 1746-IB16, 16-ch digital input
        ioConfig[2 * 6 + 4 + 0] = 2;               // InputBytes = 2

        // Slot 3 — 1746-IB16, 16-ch digital input
        ioConfig[3 * 6 + 4 + 0] = 2;               // InputBytes = 2

        // Slot 4 — 1746-OB16, 16-ch digital output
        ioConfig[4 * 6 + 4 + 2] = 2;               // OutputBytes = 2

        // Slot 5 — 1746-OB16, 16-ch digital output
        ioConfig[5 * 6 + 4 + 2] = 2;               // OutputBytes = 2

        // Slot 6 — 1746-NI4, 4-ch analog input (4 × 16-bit words) + 1 control word
        ioConfig[6 * 6 + 4 + 0] = 8;               // InputBytes  = 8 (4 channels × 2 bytes)
        ioConfig[6 * 6 + 4 + 2] = 2;               // OutputBytes = 2 (control word)

        // CardCode fields left as 0x0000 for all slots (not required for emulation)

        // ── Download init seed file (file type 0x63, file number 0) ─────────────────
        // Source: AB Publication 1747-RM001, FNC=0x88 Download Initialize sequence.
        // DF1Comm.DownloadProgramData (ProcessorType 0x49) reads 4 bytes from this file
        // and copies them verbatim into the FNC=0x88 init packet (data[8..11]).
        // FNC=0x88/0x11/0x52/0x12 are not implemented; this file exists only to let
        // the read succeed before the emulator rejects 0x88 with an error response.
        CreateDataFile(0x63, 0, 4, 4);              // content = 0x00000000 (sufficient)
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read lengthInBytes bytes starting at byte offset `element`.
    /// Returns empty array and status != 0 on error.
    /// </summary>
    public byte[] ReadRaw(int fileType, int fileNumber, int element, int lengthInBytes, out int status)
    {
        lock (_lock)
        {
            status = 0;
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) { status = 2; return Array.Empty<byte>(); }
            if (element < 0 || element >= data.Length) { status = 3; return Array.Empty<byte>(); }
            if (element + lengthInBytes > data.Length) { status = 3; return Array.Empty<byte>(); }
            byte[] result = new byte[lengthInBytes];
            Array.Copy(data, element, result, 0, lengthInBytes);
            return result;
        }
    }

    /// <summary>
    /// Write newData at byte offset: element * bytesPerElement + subElement * 2.
    /// Returns false if file not found or offset is out of range.
    /// </summary>
    public bool Write(int fileType, int fileNumber, int element, int subElement, int lengthInBytes, byte[] newData)
    {
        lock (_lock)
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            int bpe = _bytesPerElement.TryGetValue((fileType, fileNumber), out var b) ? b : 2;
            int offset = element * bpe + subElement * 2;
            if (offset < 0 || offset >= data.Length) return false;
            if (offset + lengthInBytes > data.Length) return false;
            int toWrite = Math.Min(newData.Length, data.Length - offset);
            Array.Copy(newData, 0, data, offset, toWrite);
            return true;
        }
    }

    /// <summary>
    /// Returns the element size in bytes for a given file (e.g. 2 for N7, 4 for F8).
    /// Defaults to 2 (word) if the file type/number is not registered.
    /// </summary>
    public int GetBytesPerElement(int fileType, int fileNumber)
    {
        lock (_lock)
        {
            if (_bytesPerElement.TryGetValue((fileType, fileNumber), out int bpe))
                return bpe;
            return 2;
        }
    }

    /// <summary>
    /// Returns total size in bytes of the specified file, or 0 if not found.
    /// </summary>
    public int GetFileSize(int fileType, int fileNumber)
    {
        lock (_lock)
        {
            byte[]? data = Lookup(fileType, fileNumber);
            return data?.Length ?? 0;
        }
    }

    /// <summary>
    /// Returns the file type code (e.g. 0x89 for integer) for a given file number.
    /// Uses a fast O(1) lookup array built during file creation.
    /// Returns 0 if file number is not a data file.
    /// </summary>
    public int GetFileTypeForNumber(int fileNumber)
    {
        lock (_lock)
        {
            return _fileTypeByNumber.TryGetValue(fileNumber, out var type) ? type : 0;
        }
    }

    /// <summary>
    /// GetFileInfo private helper
    /// </summary>
    private int GetFileTypeForNumberNoLock(int fileNumber)
    {
        return _fileTypeByNumber.TryGetValue(fileNumber, out var type) ? type : 0;
    }

    /// <summary>
    /// Returns file type, size in bytes, and element count for a given file number.
    /// Used by HandleReadFileInfo to build the 9-byte reply in a single call.
    /// Returns false if file number is not found.
    /// </summary>
    public bool GetFileInfo(int fileNumber, out int fileType, out int sizeBytes, out int elementCount)
    {
        fileType = 0;
        sizeBytes = 0;
        elementCount = 0;

        lock (_lock)
        {
            fileType = GetFileTypeForNumberNoLock(fileNumber);
            if (fileType == 0) return false;

            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            sizeBytes = data.Length;
            int bpe = _bytesPerElement.TryGetValue((fileType, fileNumber), out var b) ? b : 2;
            elementCount = bpe > 0 ? sizeBytes / bpe : 0;
            return true;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private byte[]? Lookup(int fileType, int fileNumber)
    {
        // Try exact match first
        if (_files.TryGetValue((fileType, fileNumber), out var data))
            return data;
        // Try without the high bit (some clients may send 0x0B instead of 0x8B)
        int keyType = fileType & 0x7F;
        if (_files.TryGetValue((keyType, fileNumber), out data))
            return data;
        // Try with high bit set (if client sent without)
        if (_files.TryGetValue((keyType | 0x80, fileNumber), out data))
            return data;
        return null;
    }

    private static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Writes a 10-byte program file entry.
    /// Since this emulator has no actual ladder logic, the size is set to 1 word
    /// (the minimum) to avoid RSLinx misinterpreting the directory.
    /// </summary>
    private static void WriteProgramFileEntry(byte[] buf, int offset, byte fileNumber)
    {
        int sizeWords = 1;   // minimal size – no actual ladder program

        buf[offset]     = 0x02;
        buf[offset + 1] = (byte)(sizeWords & 0xFF);
        buf[offset + 2] = (byte)(sizeWords >> 8);
        buf[offset + 3] = fileNumber;
        buf[offset + 4] = 0x00;
        buf[offset + 5] = 0x00;
        for (int i = 6; i < 10; i++) buf[offset + i] = 0;
    }

    private void CreateDataFile(byte fileType, int fileNumber, int sizeBytes, int bytesPerElement)
    {
        _files[(fileType, fileNumber)] = new byte[sizeBytes];
        _bytesPerElement[(fileType, fileNumber)] = bytesPerElement;
        _fileTypeByNumber[fileNumber] = fileType;   // also store in lookup array
    }
}
