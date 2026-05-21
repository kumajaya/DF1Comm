# DF1Comm – DF1 Protocol Library for .NET (C# Port)

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)

**DF1Comm** is a complete, self‑contained C# implementation of the **Allen‑Bradley DF1 full‑duplex protocol** for .NET.  

It is a **port** of the original **DF1Comm.vb** written by **Archie Jacobs of Manufacturing Automation LLC** – a proven, widely used DF1 implementation. This C# version preserves the original logic while adding:

- A reusable **DF1 communication library** (`DF1Comm`)
- A **standalone SLC 5/03 emulator** (`DF1Emulator`) for testing without real PLC hardware
- An **example client** that demonstrates all major library features

All components target .NET 8 and are licensed under **Apache 2.0**.

---

## Repository Structure

```
DF1Comm/
├── LICENSE                         (Apache 2.0)
├── README.md                       (this file)
├── src/
│   ├── DF1Comm/                    # Core library (C# port of DF1Comm.vb)
│   │   ├── CheckSumOptions.cs
│   │   ├── DF1Comm.cs
│   │   ├── DF1Exception.cs
│   │   ├── Models.cs
│   │   ├── Core/
│   │   |   ├── AddressParser.cs
│   │   |   ├── DataLink.cs
│   │   |   ├── MessageDecoder.cs
│   │   |   ├── PacketBuilder.cs
│   │   |   ├── SerialPortWrapper.cs
│   │   |   └── StringConverter.cs
│   │   └── DF1Comm.csproj
│   ├── DF1Emulator/                # SLC 5/03 emulator (standalone)
│   │   ├── DF1Emulator.cs
│   │   ├── MessageDecoder.cs
│   │   ├── PlcMemory.cs
│   │   ├── Program.cs
│   │   ├── DF1Emulator.csproj
│   │   └── README.md
│   └── Example/                    # Example client application
│       ├── Program.cs
│       ├── Example.csproj
│       └── README.md
└── DF1Comm.sln                     # Visual Studio solution
```

---

## Features

### DF1Comm Library (C# Port)
- Full‑duplex DF1 framing (DLE STX / DLE ETX with DLE stuffing)
- **BCC** (XOR) and **CRC‑16** (Modbus polynomial 0xA001) checksum support
- Read/write any data type: **integers, floats, bits, strings, timers, counters**
- Switch processor between **RUN** and **PROGRAM** modes
- Auto‑detect communication settings (baud, parity, checksum) via `DetectCommSettings()`
- Retrieve data file directory (`GetDataMemory()`)
- Upload/download complete program files (SLC style)
- Support for **SLC 5/03**, **MicroLogix 1500**, and many other DF1‑compatible PLCs

### DF1Emulator (Standalone Tool)
- Emulates an **SLC 5/03** DF1 port (processor type `0x49`)
- Implements the full DF1 link layer: ACK/NAK, ENQ handling, checksum validation
- In‑memory file system with pre‑defined data files (O0, I1, S2, B3, N7, F8, T4, C5, R6, and additional B/N files up to file 31)
- Responds to **Get Status** (CMD 0x06 FNC 0x03) with realistic 24‑byte payload
- Handles **Protected Typed Logical Read/Write** (0xA1, 0xA2, 0xAA, 0xAB)
- Configurable node ID, checksum, baud rate, parity via command line
- Console hex logging for debugging

### Example Client
- Demonstrates all major library APIs
- Reads processor type, data files, and specific addresses
- Writes integers, floats, and bits
- Switches RUN/PROGRAM mode
- Can be used with **real PLC** or **DF1Emulator** over a virtual serial pair

---

## Attribution

This C# library is a **direct port** of the original **DF1Comm.vb** written by **Archie Jacobs, Manufacturing Automation LLC**.  

The original Visual Basic code has been faithfully translated to C#, preserving the DF1 logic, error handling, and timing behaviour. All enhancements (emulator, example client, modern .NET packaging) are built on top of that core.

We thank Archie Jacobs for providing a robust, well‑tested DF1 implementation to the industrial automation community.

---

## Requirements

- **.NET 8 SDK** or later
- Windows / Linux / macOS (serial port support required)
- For testing without hardware: a **virtual serial port emulator** (e.g. com0com on Windows, `socat` on Linux)

---

## Build

Clone the repository and build the whole solution:

```bash
git clone https://github.com/kumajaya/DF1Comm.git
cd DF1Comm
dotnet build -c Release DF1Comm.sln
```

Individual projects can also be built separately:

```bash
dotnet build -c Release src/DF1Comm/DF1Comm.csproj
dotnet build -c Release src/DF1Emulator/DF1Emulator.csproj
dotnet build -c Release src/Example/Example.csproj
```

---

## Usage

### 1. Using the DF1Comm Library

```csharp
using DF1Comm.Communication;

var df1 = new DF1Comm("COM1", 19200, Parity.None)
{
    TargetNode = 1,
    MyNode = 0,
    CheckSum = CheckSumOptions.Crc
};

df1.OpenComms();

// Read processor type
int procType = df1.GetProcessorType();
Console.WriteLine($"Processor: 0x{procType:X2}");

// Read an integer from N7:0
string value = df1.ReadAny("N7:0");
Console.WriteLine($"N7:0 = {value}");

// Write a float to F8:1
df1.WriteData("F8:1", 3.14159f);

// Set RUN mode
df1.SetRunMode();

df1.CloseComms();
```

### 2. Running the Emulator

```bash
dotnet run --project src/DF1Emulator -- COM2 --baud 19200 --checksum bcc
```

See `src/DF1Emulator/README.md` for full emulator documentation.

### 3. Running the Example Client

```bash
dotnet run --project src/Example -- COM1 --target 1 --checksum crc
```

See `src/Example/README.md` for client details.

### 4. Testing Emulator + Client Together

1. Create a virtual serial pair (e.g. `COM1` ↔ `COM2`).
2. Start emulator on `COM2`:
   ```bash
   dotnet run --project src/DF1Emulator -- COM2 --checksum crc
   ```
3. In another terminal, start the example client on `COM1`:
   ```bash
   dotnet run --project src/Example -- COM1 --checksum crc
   ```

---

## Protocol Reference

The implementation follows **Allen‑Bradley Publication 1770‑6.5.16** (DF1 Protocol and Command Set). Supported commands include:

| Command | Description |
|---------|-------------|
| `0x06` (Get Status) | Read processor type, mode, diagnostics |
| `0x0F` (Protected Typed Logical Read/Write) | Read/write data files (0xA1, 0xA2, 0xAA, 0xAB) |
| `0x01` (Reset) | Reset communication |
| `0x0B` (Set Variables) | Configure communication parameters (RSLinx auto‑configure) |
| `0x0A` (Diagnostic Counters) | Read modem and packet statistics |
| `0x67` (Read Modified Data) | Simplified read variant |
| `0xF` (Execute Command List) | Multi‑function commands (mode change, I/O config, upload/download) |

Checksum modes:
- **BCC**: XOR of all bytes in the payload, then two's complement.
- **CRC‑16**: Initial value `0x0000`, polynomial `0xA001` (Modbus), ETX byte `0x03` included.

---

## Troubleshooting

| Issue | Likely solution |
|-------|------------------|
| `No response, Check COM Settings` | Verify port, baud rate, parity, and that the target is powered and connected. |
| `Checksum mismatch` | Ensure both sides use the same checksum mode (`--checksum crc` or `bcc`). |
| `Illegal Command or Format` | The target may not support the addressed file/element. Check file numbers and element bounds. |
| `Processor is in Program mode` | Normal – writes may be restricted. Use `SetRunMode()` to change. |
| `Port busy` | Only one application can open a COM port at a time. Close other programs (RSLinx, etc.). |
| `RSLinx does not see memory` | Enable verbose logging in the emulator and verify `Read File 0` requests are answered correctly. |

---

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/amazing-feature`).
3. Commit your changes (`git commit -m 'Add amazing feature'`).
4. Push to the branch (`git push origin feature/amazing-feature`).
5. Open a Pull Request.

Please keep all code **self‑contained** (avoid external dependencies except `System.IO.Ports`). Add unit tests when possible.

---

## License

**The original DF1Comm.vb** (by Archie Jacobs, Manufacturing Automation LLC) was released under the **GNU General Public License**.  

**This C# port and all additional components (emulator, example client, modernised project structure) are licensed under the Apache License, Version 2.0.**  

```
Copyright (c) 2026 Ketut Kumajaya

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

Full text in [`LICENSE`](LICENSE) file.

---

## Acknowledgements

- **Archie Jacobs, Manufacturing Automation LLC** – for the original VB DF1Comm implementation that made this port possible.
- **Allen‑Bradley / Rockwell Automation** – for the DF1 protocol specification (Publication 1770‑6.5.16).

---

## Related Projects

- [DF1Emulator](src/DF1Emulator/README.md) – standalone emulator
- [Example Client](src/Example/README.md) – usage demonstration
