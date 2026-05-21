# DF1Comm Example Client

**Purpose**  
Demonstration client for the `DF1Comm` library. It communicates with a DF1‑compatible device (real PLC or emulator) over a serial port, performs typical read/write operations, and displays the results.

## Features
- Reads processor type (Get Status, CMD 0x06 FNC 0x03)
- Reads/writes integers (`N7`, `O0`, `I1`, `B3`)
- Reads/writes floating‑point values (`F8`)
- Bit‑level write (`B3:0/0`, `/3`)
- Switches processor between **RUN** and **PROGRAM** mode
- Retrieves data file directory (`GetDataMemory()`)
- Configurable serial settings (port, baud, parity, node IDs, checksum)

## Requirements
- .NET 8 SDK or later
- `DF1Comm` library (referenced via project or DLL)
- A DF1 target – either:
  - A real SLC 5/03 or MicroLogix PLC with DF1 port
  - The **DF1Emulator** (standalone emulator) connected via virtual serial pair

## Build

### With project reference (typical)
```bash
dotnet build -c Release Example.csproj
```

### If `DF1Comm` is a separate project in the same solution
Ensure the solution includes both `DF1Comm.csproj` and `Example.csproj` with a `ProjectReference`.

## Run

**Default** (COM1, 19200, no parity, target node 1, local node 0, CRC checksum):
```bash
dotnet run --project Example.csproj -- COM1
```

### Command line options
| Option | Description | Default |
|--------|-------------|---------|
| `[port]` | Serial port name | `COM1` |
| `--baud <n>` | Baud rate | `19200` |
| `--parity <none/odd/even>` | Parity mode | `none` |
| `--target <n>` | Target PLC node ID | `1` |
| `--mynode <n>` | Local/master node ID | `0` |
| `--checksum <crc/bcc>` | Checksum mode | `crc` |
| `--help, -h` | Show usage | – |

### Example with DF1Emulator (virtual pair)
1. Create virtual serial pair, e.g. `COM1` ↔ `COM2`.
2. Start the emulator on `COM2` (using BCC or CRC as needed):
   ```bash
   dotnet run --project DF1Emulator.csproj -- COM2 --checksum crc
   ```
3. In another terminal, run the example client on `COM1`:
   ```bash
   dotnet run --project Example.csproj -- COM1 --target 1 --checksum crc
   ```

## Expected output (successful run)
```
Connected on COM2
Baud=19200, Parity=None, Checksum=Crc
MyNode=0, TargetNode=1

Processor Type: 0x49 (SLC 5/03 = 0x49)

--- Read Operations ---
O0:0 = 513
I1:0 = 0
B3:0 = 43690
N7:0 = 123
F8:0 = 1.23
PLC is in RUN mode

--- Data Files ---
File 0: Type=1 Elements=...
File 1: Type=139 Elements=...
...

--- Write Operations ---
Writing 999 to N7:1...
Writing 2.718 to F8:1...
Setting B3:0/0 = 1...
Setting B3:0/3 = 1...
Switching to PROGRAM mode...

--- Read Operations ---
N7:1 = 999
F8:1 = 2.718
B3:0 = 43691
PLC is in PROGRAM mode
```

## Troubleshooting
| Issue | Solution |
|-------|----------|
| `No response, Check COM Settings` | Verify the COM port is correct, baud rate/parity match the target, and the target is powered on. |
| `Checksum mismatch` | Ensure `--checksum` matches the target’s setting (emulator default is BCC, example default is CRC). |
| `Illegal Command or Format` | The target may not support the addressed file/element. Check file numbers and element bounds. |
| `Processor is in Program mode` | Normal – writes may be allowed but some commands are restricted. Use `SetRunMode()` to change. |
| `Access denied` | Some DF1 targets have command protection. Not supported by this example. |

## Extending the example
- Add support for **timers** (T4) and **counters** (C5) using `ReadAny("T4:0.ACC")`
- Implement **string** read/write (ST9 file)
- Use `ReadRawData()` / `WriteRawData()` for binary block transfers

## License
Same as the DF1Comm library.

## See also
- [DF1Emulator](https://github.com/kumajaya/DF1Emulator) – standalone emulator for testing
- [DF1Comm Library Documentation](https://github.com/kumajaya/DF1Comm)
