# DF1 SLC 5/03 Emulator

**Purpose**  
Lightweight, standalone DF1 RS-232 emulator that mimics an SLC 5/03 PLC for testing DF1 clients and RSLinx. This emulator does **not** depend on any external DF1 library – all DF1 framing, checksum (BCC/CRC), DLE stuffing, and memory simulation are self‑contained.

## Features
- DF1 full‑duplex framing with DLE STX / DLE ETX and DLE stuffing
- CRC‑16 (calculation as per AB specification) **default** – BCC (XOR) also supported
- Get Status response crafted for SLC 5/03 (processor code `0x49`)
- Reads from File 0 (directory) and any data file listed in the directory
- In‑memory PLC file store with pre‑defined files (O, I, S, B, N, F, T, C, R)
- Configurable serial settings via command line
- Console logging of RX/TX hex for debugging
- Independent – no external dependencies except `System.IO.Ports`

## Requirements
- .NET 8 SDK or later
- Virtual serial pair tool for testing (e.g., com0com on Windows)
- RSLinx Classic (optional, for integration testing)

## Build
```bash
dotnet build -c Release DF1Emulator.csproj
```

## Run

**Default** (COM2, 19200, no parity, node 1, CRC checksum):
```bash
dotnet run --project DF1Emulator.csproj -- COM2
```

**Examples:**
```bash
# Use BCC checksum, node 2
dotnet run --project DF1Emulator.csproj -- COM2 --checksum bcc --node 2

# Change baud rate and parity
dotnet run --project DF1Emulator.csproj -- COM3 --baud 9600 --parity even
```

### Command line options
| Option | Description | Default |
|--------|-------------|---------|
| `[port]` | Serial port name | `COM2` |
| `--baud <n>` | Baud rate | `19200` |
| `--parity <none/odd/even>` | Parity mode | `none` |
| `--node <n>` | Emulator node ID | `1` |
| `--checksum <crc/bcc>` | Checksum mode | `crc` |
| `--help, -h` | Show usage | – |

## Quick test with virtual serial pair
1. Create a virtual COM pair (e.g., `COM1` ↔ `COM2` using com0com).
2. Start the emulator on `COM2`:
   ```bash
   dotnet run --project DF1Emulator.csproj -- COM2
   ```
3. Start your DF1 client (any DF1 master, e.g., DF1Comm or RSLinx) on `COM1`.
4. Send a **Get Status** (CMD 0x06, FNC 0x03). The emulator replies with processor type `0x49` (SLC 5/03).

## RSLinx integration
- Create an **RS-232 DF1** driver in RSLinx.
- Point it to the COM port paired with the emulator.
- Use **19200 baud, No parity, 8 data bits, 1 stop bit** (or match the emulator settings).
- If RSLinx does not show the processor or memory, check the emulator console for hex logs and ensure `PlcMemory` file offsets match RSLinx expectations.

## Project structure
| File | Description |
|------|-------------|
| `Program.cs` | CLI entry point, argument parsing, usage help |
| `DF1Emulator.cs` | Core DF1 frame parsing, command handlers, ACK/NAK, serial I/O |
| `MessageDecoder.cs` | DLE stuffing/unstuffing, BCC/CRC checksum calculation |
| `PlcMemory.cs` | In‑memory file directory (File 0) and data files (O0, I1, S2, B3, N7, F8, T4, C5, R6, etc.) |

## Extending the emulator
- **Add new DF1 commands** – extend the dispatch logic in `DF1Emulator.ProcessFrame()`.
- **Add new data files** – modify `PlcMemory` constructor and the file directory inside `File 0`.
- **Change element sizes** – update `_bytesPerElement` dictionary and file size arrays.
- **Simulate timers/counters** – implement background thread that updates T4 and C5 structures.

## Troubleshooting
| Issue | Likely solution |
|-------|------------------|
| **Port busy / access denied** | Ensure no other application (including RSLinx) is using the same COM port. Use a virtual pair so emulator and client use different ends. |
| **Checksum mismatch** | Verify the client and emulator use the same checksum type (`--checksum crc` or `bcc`). Emulator defaults to **CRC**. |
| **RSLinx cannot see memory** | Enable verbose logging in emulator. Confirm that `Read File 0` requests are received and that the emulator responds with a valid directory. Check file offsets in `PlcMemory`. |
| **No response after ENQ** | Make sure the emulator is running on the correct COM port and the baud rate/parity matches the client. The emulator automatically replies with ACK to ENQ. |

## License
Same as the DF1Comm library.

## Contributing
- Fork, create a feature branch, and open a pull request.
- Keep the code **self‑contained** (no external DF1 library dependencies).
- Add unit tests for new features (mock `SerialPort` if needed).
