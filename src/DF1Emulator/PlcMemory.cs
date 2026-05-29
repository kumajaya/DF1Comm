// SPDX-License-Identifier: GPL-3.0-or-later
// 
// DF1Comm - DF1 Protocol Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Based on original DF1Comm.vb by Archie Jacobs (Manufacturing Automation LLC)
// which was released under GPLv2-or-later.
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

/// <summary>
/// In-memory PLC file store simulating an SLC 5/03 (1747-L532E).
/// Memory layout follows AB Publication 1770-6.5.16.
///
/// Directory structure (fileType=1, fileNumber=0):
///   offset 46/47 = number of program files (little-endian)
///   offset 52/53 = number of data tables   (little-endian)
///   offset 70/71 = total directory size in bytes
///   offset 79..  = file table, 10 bytes per entry:
///                  [type, sizeWords_lo, sizeWords_hi, fileNum, attr, elemSize, addrLo, addrHi, 0, 0]
///
/// ReadRaw: `element` parameter is a raw byte offset into the file array.
///          DF1Comm computes: element * bytesPerElement + subElement * 2.
///
/// Data files (verified against RSLogix 500 upload — Total Files=32, Active=21):
///
///   File  Type  Elem  Bytes  Notes
///   ────  ────  ────  ─────  ─────────────────────────────────────────────
///      0  O     2     4      Output image: O:0 (slot 4), O:1 (slot 5)
///      1  I     7     14     Input image:  I:0–I:2 (slots 1–3), I:3–I:6 (slot 6 NI4)
///      2  S     83    166    Status S:0–S:82; WORDS=0 in directory (system memory)
///      3  B     14    28     B3:0–B3:13
///      4  T     78    468    T4:0–T4:77,  6 bytes/elem
///      5  C     1     6      C5:0,        6 bytes/elem
///      6  R     2     12     R6:0–R6:1,   6 bytes/elem
///      7  N     74    148    N7:0–N7:73
///      8  F     38    152    F8:0–F8:37,  4 bytes/elem
///      9  B     10    20     B9:0–B9:9
///     10  B     71    142    B10:0–B10:70
///     11  B     9     18     B11:0–B11:8
///     12  B     1     2      B12:0
///     13  B     2     4      B13:0–B13:1
///     14  B     1     2      B14:0
///     15  B     41    82     B15:0–B15:40
///     16  B     41    82     B16:0–B16:40
///     17  N     26    52     N17:0–N17:25
///  18–28  —     —     —      Inactive slots (type 0x8D, size 0)
///     29  B     26    52     B29:0–B29:25
///     30  B     26    52     B30:0–B30:25
///     31  B     26    52     B31:0–B31:25
///
/// Program files (Total=24, Active=10):
///   File 0–1: SYS; Files 2–23: LAD (active: 2, 3, 5, 8, 12, 15, 18, 19, 22, 23)
///   LAD file size = rungs × 2 words (realistic estimate)
///
/// Rack (1746-A7, 7 slots):
///   Slot 0: 1747-L532E  CPU           no I/O image
///   Slot 1: 1746-IB16   Digital In    2 InputBytes
///   Slot 2: 1746-IB16   Digital In    2 InputBytes
///   Slot 3: 1746-IB16   Digital In    2 InputBytes
///   Slot 4: 1746-OB16   Digital Out   2 OutputBytes
///   Slot 5: 1746-OB16   Digital Out   2 OutputBytes
///   Slot 6: 1746-NI4    Analog In     8 InputBytes (4 ch × 2 bytes)
/// </summary>
public class PlcMemory
{
    private readonly Dictionary<(int, int), byte[]> _files          = new();
    private readonly Dictionary<(int, int), int>    _bytesPerElement = new();
    private readonly Dictionary<int, int>           _fileTypeByNumber = new();
    private readonly object _lock = new object();

    public PlcMemory()
    {
        BuildDirectory();
        BuildDataFiles();
        BuildIoConfig();
        BuildDownloadSeed();
    }

    // =========================================================================
    // DIRECTORY
    // =========================================================================

    private void BuildDirectory()
    {
        // Directory size calculation:
        //   79 bytes header + 56 entries × 10 bytes = 639 bytes total
        //   56 entries = 32 data file slots + 24 program file slots
        //
        // Note: This 639 bytes is the size of File 0 (directory) itself.
        //       It is NOT the "Total Memory (Words): 714" shown in RSLogix.
        //       The 714 words is the sum of user data table words (O, I, B, N, F, T, C, R files)
        //       which is calculated from sizeBytes of each data file registered below.
        //
        const int dirSize = 639;
        var dir = new byte[dirSize];

        WriteU16(dir, 70, dirSize); // directory size at offset 70 (element 0x23 × 2)
        WriteU16(dir, 46, 24);      // 24 program files: SYS×2 + LAD×22
        WriteU16(dir, 52, 32);      // 32 data file slots

        int pos  = 79;   // file table starts at offset 79
        int addr = 0;    // running base address in WORDS

        // Write a 10-byte data file entry.
        // sizeBytes is the actual in-memory size; stored as WORDS in the directory.
        // S2 passes sizeBytes=0: its directory entry (type=0x84, sizeWords=0) is
        // written normally, but addr does not advance — the status file lives in
        // system memory and does not consume user data table address space.
        void Reg(byte type, int sizeBytes, byte fileNum, int elemSize = 2)
        {
            int sizeWords = sizeBytes / 2;
            dir[pos]     = type;
            dir[pos + 1] = (byte)(sizeWords & 0xFF);
            dir[pos + 2] = (byte)(sizeWords >> 8);
            dir[pos + 3] = fileNum;
            dir[pos + 4] = 0x00;              // attribute: normal
            dir[pos + 5] = (byte)elemSize;    // element size in bytes
            dir[pos + 6] = (byte)(addr & 0xFF);
            dir[pos + 7] = (byte)(addr >> 8);
            dir[pos + 8] = 0x00;
            dir[pos + 9] = 0x00;
            addr += sizeWords;
            _fileTypeByNumber[fileNum] = type;
            pos += 10;
        }

        // ── Data files ───────────────────────────────────────────────────────
        Reg(0x8B,   4,  0);       // O0  — 2 elem × 2 bytes
        Reg(0x8C,  14,  1);       // I1  — 7 elem × 2 bytes
        Reg(0x84,   0,  2);       // S2  — system memory, no user address space
        Reg(0x85,  28,  3);       // B3  — 14 words
        Reg(0x86, 468,  4, 6);    // T4  — 78 elem × 6 bytes
        Reg(0x87,   6,  5, 6);    // C5  — 1 elem  × 6 bytes
        Reg(0x88,  12,  6, 6);    // R6  — 2 elem  × 6 bytes
        Reg(0x89, 148,  7);       // N7  — 74 words
        Reg(0x8A, 152,  8, 4);    // F8  — 38 elem × 4 bytes
        Reg(0x85,  20,  9);       // B9  — 10 words
        Reg(0x85, 142, 10);       // B10 — 71 words
        Reg(0x85,  18, 11);       // B11 — 9 words
        Reg(0x85,   2, 12);       // B12 — 1 word
        Reg(0x85,   4, 13);       // B13 — 2 words
        Reg(0x85,   2, 14);       // B14 — 1 word
        Reg(0x85,  82, 15);       // B15 — 41 words
        Reg(0x85,  82, 16);       // B16 — 41 words
        Reg(0x89,  52, 17);       // N17 — 26 words

        // Files 18–28: inactive slots (type 0x85 = Binary, size 0).
        // RSLogix shows Total Files=32, Active Files=21; these occupy directory
        // slots but have no data and do not appear in RSLogix file lists.
        for (int n = 18; n <= 28; n++)
        {
            dir[pos]     = 0x85;
            dir[pos + 1] = 0x00;
            dir[pos + 2] = 0x00;
            dir[pos + 3] = (byte)n;
            // bytes 4–9 all zero
            pos += 10;
        }

        Reg(0x85, 52, 29);        // B29 — 26 words
        Reg(0x85, 52, 30);        // B30 — 26 words
        Reg(0x85, 52, 31);        // B31 — 26 words

        // ── Program files ────────────────────────────────────────────────────
        // Rung counts per file from baseline, with realistic size: 2 words per rung
        var rungs = new Dictionary<int, int>
        {
            {2, 23}, {3, 13}, {5, 24}, {8, 26}, {12, 16},
            {15, 22}, {18, 18}, {19, 6}, {22, 14}, {23, 9}
        };
        int[] activeLad = { 2, 3, 5, 8, 12, 15, 18, 19, 22, 23 };
        const int wordsPerRung = 2;   // realistic estimate: 2 words per rung

        // SYS file 0
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x01;   // size = 1 word
        dir[pos + 3] = 0x00;
        pos += 10;
        _fileTypeByNumber[0] = 0x01;

        // SYS file 1
        dir[pos]     = 0x01;
        dir[pos + 1] = 0x01;   // size = 1 word
        dir[pos + 3] = 0x01;
        pos += 10;
        _fileTypeByNumber[1] = 0x01;

        // LAD files 2–23
        for (int n = 2; n <= 23; n++)
        {
            bool active = Array.IndexOf(activeLad, n) >= 0;
            int sizeWords = active ? rungs[n] * wordsPerRung : 0;
            
            // Use FileType in the range 0x20-0x3F
            byte fileType = (byte)(0x20 + (n - 2));
            
            dir[pos]     = fileType;
            dir[pos + 1] = (byte)(sizeWords & 0xFF);
            dir[pos + 2] = (byte)((sizeWords >> 8) & 0xFF);
            dir[pos + 3] = (byte)n;
            // bytes 4–9 remain zero
            pos += 10;

            _fileTypeByNumber[n] = fileType;
            // Allocate storage for ladder logic (used when writing to program files)
            _files[(fileType, n)] = new byte[sizeWords * 2];
            _bytesPerElement[(fileType, n)] = 0;
        }

        _files[(1, 0)] = dir;
    }

    // =========================================================================
    // DATA FILES
    // =========================================================================

    private void BuildDataFiles()
    {
        // ── O0 — Output image (2 elem = 4 bytes) ─────────────────────────────
        // O:0 = slot 4 (1746-OB16), O:1 = slot 5 (1746-OB16). Last address: O:1.
        _files[(0x8B, 0)]          = new byte[4];
        _bytesPerElement[(0x8B, 0)] = 2;

        // ── I1 — Input image (7 elem = 14 bytes) ─────────────────────────────
        // I:0–I:2 = slots 1–3 (1746-IB16 × 3), I:3–I:6 = slot 6 (1746-NI4, 4 ch).
        // Last address: I:6.
        _files[(0x8C, 1)]          = new byte[14];
        _bytesPerElement[(0x8C, 1)] = 2;

        // ── S2 — Status (83 elem = 166 bytes, S:0–S:82) ──────────────────────
        // WORDS=0 in directory: system memory, not counted in user Total Memory.
        _files[(0x84, 2)]          = new byte[166];
        _bytesPerElement[(0x84, 2)] = 2;
        ushort[] s2 =
        {
            // S2:0  – S2:9
            0x0004, 0x001E, 0x9012, 0xA003, 0x69C4, 0x0000, 0x0000, 0x0000, 0x0000, 0x0003,
            // S2:10 – S2:19
            0x0000, 0x0000, 0x0000, 0x0000, 0x001E, 0x0401, 0x0016, 0x0002, 0x0000, 0x0000,
            // S2:20 – S2:29
            0x0000, 0x0000, 0x0031, 0x000C, 0x0020, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:30 – S2:39
            0x0000, 0x0000, 0x0018, 0x0000, 0x007D, 0x0000, 0x07EA, 0x0005, 0x0015, 0x0000,
            // S2:40 – S2:49
            0x0000, 0x0034, 0x002E, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            // S2:50 – S2:59
            0x0000, 0x0000, 0x0000, 0x0004, 0x0000, 0x0000, 0x012E, 0x012E, 0x0004, 0x0000,
            // S2:60 – S2:69
            0x0214, 0x0004, 0x0008, 0x0001, 0x005F, 0x0010, 0x01E0, 0x0006, 0x0000, 0x0000,
            // S2:70 – S2:82
            0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
            0x0000, 0x0000, 0x0000,
        };
        for (int i = 0; i < s2.Length; i++)
            WriteU16(_files[(0x84, 2)], i * 2, s2[i]);

        // ── B3 — Binary (14 words = 28 bytes) ────────────────────────────────
        _files[(0x85, 3)]          = new byte[28];
        _bytesPerElement[(0x85, 3)] = 2;
        WriteU16(_files[(0x85, 3)], 0, 0xAA55);
        WriteU16(_files[(0x85, 3)], 2, 0x0FF0);

        // ── T4 — Timer (78 elem × 6 bytes = 468 bytes) ───────────────────────
        _files[(0x86, 4)]          = new byte[468];
        _bytesPerElement[(0x86, 4)] = 6;

        // ── C5 — Counter (1 elem × 6 bytes) ──────────────────────────────────
        _files[(0x87, 5)]          = new byte[6];
        _bytesPerElement[(0x87, 5)] = 6;

        // ── R6 — Control (2 elem × 6 bytes = 12 bytes) ───────────────────────
        _files[(0x88, 6)]          = new byte[12];
        _bytesPerElement[(0x88, 6)] = 6;

        // ── N7 — Integer (74 words = 148 bytes) ──────────────────────────────
        _files[(0x89, 7)]          = new byte[148];
        _bytesPerElement[(0x89, 7)] = 2;
        WriteU16(_files[(0x89, 7)],  0,   123);
        WriteU16(_files[(0x89, 7)],  2,   456);
        WriteU16(_files[(0x89, 7)],  4, -789);

        // ── F8 — Float (38 elem × 4 bytes = 152 bytes) ───────────────────────
        _files[(0x8A, 8)]          = new byte[152];
        _bytesPerElement[(0x8A, 8)] = 4;
        Array.Copy(BitConverter.GetBytes(1.23f), 0, _files[(0x8A, 8)], 0, 4);
        Array.Copy(BitConverter.GetBytes(4.56f), 0, _files[(0x8A, 8)], 4, 4);

        // ── B9–B16, N17, B29–B31 ─────────────────────────────────────────────
        CreateDataFile(0x85,  9,  20, 2);   // B9  — 10 words
        CreateDataFile(0x85, 10, 142, 2);   // B10 — 71 words
        CreateDataFile(0x85, 11,  18, 2);   // B11 — 9 words
        CreateDataFile(0x85, 12,   2, 2);   // B12 — 1 word
        CreateDataFile(0x85, 13,   4, 2);   // B13 — 2 words
        CreateDataFile(0x85, 14,   2, 2);   // B14 — 1 word
        CreateDataFile(0x85, 15,  82, 2);   // B15 — 41 words
        CreateDataFile(0x85, 16,  82, 2);   // B16 — 41 words
        CreateDataFile(0x89, 17,  52, 2);   // N17 — 26 words
        CreateDataFile(0x85, 29,  52, 2);   // B29 — 26 words
        CreateDataFile(0x85, 30,  52, 2);   // B30 — 26 words
        CreateDataFile(0x85, 31,  52, 2);   // B31 — 26 words
    }

    // =========================================================================
    // I/O CONFIGURATION  (file type 0x60, file number 0)
    // =========================================================================

    private void BuildIoConfig()
    {
        // Accessed via CMD=0x0F FNC=0xA2.
        // GetSlotCount()    reads byte [0] → raw slot count; returns (raw - 1).
        // GetSLCIOConfig()  reads result[i] from offset i*6+4:
        //   +0 = InputBytes, +2 = OutputBytes, +4/+5 = CardCode.
        // Slot 0 (CPU) at offset 4: InputBytes=0, OutputBytes=0 (default zero).
        // Buffer = 4 + 8*6 + 2 = 54 bytes minimum; padded to 64.
        CreateDataFile(0x60, 0, 64, 2);
        byte[] io = _files[(0x60, 0)];

        io[0] = 8;      // raw slot count → GetSlotCount() returns 7

        // InputBytes  @ slot*6+4, OutputBytes @ slot*6+6
        // Slot 0 (CPU): both zero — default array value.
        io[1 * 6 + 4] = 2;     // Slot 1: 1746-IB16  InputBytes=2
        io[2 * 6 + 4] = 2;     // Slot 2: 1746-IB16  InputBytes=2
        io[3 * 6 + 4] = 2;     // Slot 3: 1746-IB16  InputBytes=2
        io[4 * 6 + 6] = 2;     // Slot 4: 1746-OB16  OutputBytes=2
        io[5 * 6 + 6] = 2;     // Slot 5: 1746-OB16  OutputBytes=2
        io[6 * 6 + 4] = 8;     // Slot 6: 1746-NI4   InputBytes=8 (4 ch × 2 bytes)
    }

    // =========================================================================
    // DOWNLOAD SEED  (file type 0x63, file number 0)
    // =========================================================================

    private void BuildDownloadSeed()
    {
        // DF1Comm.DownloadProgramData reads 4 bytes from this file and copies them
        // into the FNC=0x88 init packet. Content 0x00000000 is sufficient.
        CreateDataFile(0x63, 0, 4, 4);
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Read <paramref name="lengthInBytes"/> bytes starting at raw byte offset
    /// <paramref name="element"/> within the specified file.
    /// Returns an empty array and sets <paramref name="status"/> != 0 on error:
    ///   2 = file not found, 3 = offset or length out of range.
    /// </summary>
    public byte[] ReadRaw(int fileType, int fileNumber, int element, int lengthInBytes, out int status)
    {
        lock (_lock)
        {
            status = 0;
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null)                              { status = 2; return Array.Empty<byte>(); }
            if (element < 0 || element >= data.Length)    { status = 3; return Array.Empty<byte>(); }
            if (element + lengthInBytes > data.Length)    { status = 3; return Array.Empty<byte>(); }
            var result = new byte[lengthInBytes];
            Array.Copy(data, element, result, 0, lengthInBytes);
            return result;
        }
    }

    /// <summary>
    /// Write <paramref name="newData"/> at byte offset
    /// <c>element * bytesPerElement + subElement * 2</c>.
    /// Returns false if the file is not found or the offset is out of range.
    /// </summary>
    public bool Write(int fileType, int fileNumber, int element, int subElement,
                      int lengthInBytes, byte[] newData)
    {
        lock (_lock)
        {
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            int bpe    = _bytesPerElement.TryGetValue((fileType, fileNumber), out var b) ? b : 2;
            int offset = element * bpe + subElement * 2;
            if (offset < 0 || offset >= data.Length)       return false;
            if (offset + lengthInBytes > data.Length)      return false;
            Array.Copy(newData, 0, data, offset, Math.Min(newData.Length, data.Length - offset));
            return true;
        }
    }

    /// <summary>Returns the bytes-per-element for a file, or 2 if not registered.</summary>
    public int GetBytesPerElement(int fileType, int fileNumber)
    {
        lock (_lock)
            return _bytesPerElement.TryGetValue((fileType, fileNumber), out int bpe) ? bpe : 2;
    }

    /// <summary>Returns the total byte size of a file, or 0 if not found.</summary>
    public int GetFileSize(int fileType, int fileNumber)
    {
        lock (_lock)
            return Lookup(fileType, fileNumber)?.Length ?? 0;
    }

    /// <summary>Returns the file type code for a given file number, or 0 if not found.</summary>
    public int GetFileTypeForNumber(int fileNumber)
    {
        lock (_lock)
            return _fileTypeByNumber.TryGetValue(fileNumber, out var t) ? t : 0;
    }

    /// <summary>
    /// Returns file type, byte size, and element count for a given file number.
    /// Used by HandleReadFileInfo. Returns false if the file number is not registered.
    /// </summary>
    public bool GetFileInfo(int fileNumber, out int fileType, out int sizeBytes, out int elementCount)
    {
        fileType = sizeBytes = elementCount = 0;
        lock (_lock)
        {
            if (!_fileTypeByNumber.TryGetValue(fileNumber, out fileType) || fileType == 0)
                return false;
            byte[]? data = Lookup(fileType, fileNumber);
            if (data == null) return false;
            sizeBytes    = data.Length;
            int bpe      = _bytesPerElement.TryGetValue((fileType, fileNumber), out var b) ? b : 2;
            elementCount = bpe > 0 ? sizeBytes / bpe : 0;
            return true;
        }
    }

    // =========================================================================
    // PRIVATE HELPERS
    // =========================================================================

    private byte[]? Lookup(int fileType, int fileNumber)
    {
        if (_files.TryGetValue((fileType, fileNumber), out var d)) return d;
        int t = fileType & 0x7F;
        if (_files.TryGetValue((t, fileNumber), out d))        return d;
        if (_files.TryGetValue((t | 0x80, fileNumber), out d)) return d;
        return null;
    }

    private void CreateDataFile(byte fileType, int fileNumber, int sizeBytes, int bytesPerElement)
    {
        _files[(fileType, fileNumber)]          = new byte[sizeBytes];
        _bytesPerElement[(fileType, fileNumber)] = bytesPerElement;
        _fileTypeByNumber[fileNumber]            = fileType;
    }

    private static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
