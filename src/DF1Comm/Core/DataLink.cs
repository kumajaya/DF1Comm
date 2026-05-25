namespace DF1Comm.Core;

/// <summary>
/// Data Link Layer for DF1 (RS-232) and DH485.
/// Handles DLE stuffing, framing, checksum (CRC/BCC), ACK/NAK, and backoff.
/// Maintains synchronous polling behavior identical to the original VB code.
/// </summary>
public class DataLink : IDisposable
{
    // ─── Constants ─────────────────────────────────────────────────────────────
    private const byte DLE = 0x10;
    private const byte STX = 0x02;
    private const byte ETX = 0x03;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;
    private const byte ENQ = 0x05;

    // ─── Fields ───────────────────────────────────────────────────────────────
    private readonly ISerialPort _port;
    private readonly object _rxLock = new object();
    private readonly List<byte> _rxBuffer = new List<byte>();
    private DateTime _frameStartTime = DateTime.MinValue;
    private const int FrameTimeoutMs = 500;
    private const int MaxBufferBytes = 4096;

    private CheckSumOptions _checksumType = CheckSumOptions.Crc;
    private int _sleepDelay = 0;          // backoff delay (ms) after NAK
    private volatile bool _lastResponseWasNAK = false;

    // For ACK/NAK polling in SendFrame
    private bool _ackReceived;
    private bool _nakReceived;
    private readonly object _ackLock = new object();

    // For ENQ polling (used by DF1Comm.SendENQ)
    private bool _ackFlagForEnq;
    private bool _nakFlagForEnq;

    // ─── Events ───────────────────────────────────────────────────────────────
    /// <summary>Raised when an application PDU (without DLE stuffing) has been received and is valid.</summary>
    public event EventHandler<byte[]>? PacketReceived;

    /// <summary>Raised when the remote sends ENQ (0x10 0x05).</summary>
    public event EventHandler? EnqReceived;

    // ─── Properties ───────────────────────────────────────────────────────────
    public CheckSumOptions ChecksumType
    {
        get => _checksumType;
        set => _checksumType = value;
    }

    /// <summary>Delay (ms) added after a NAK, before retry. Also used to delay the ACK flag.</summary>
    public int SleepDelay
    {
        get => _sleepDelay;
        set => _sleepDelay = value < 0 ? 0 : value;
    }

    public void ResetSleepDelay()
    {
        _sleepDelay = 0;
    }

    /// <summary>True if the last received message was a NAK (from remote) or checksum failed.</summary>
    public bool LastResponseWasNAK
    {
        get => _lastResponseWasNAK;
        private set => _lastResponseWasNAK = value;
    }

    /// <summary>For auto‑detect: maximum number of ticks (20 ms per tick) to wait for ACK/NAK.</summary>
    public int MaxTicks { get; set; } = 100;

    /// <summary>The last received ACK flag (used for ENQ polling).</summary>
    public bool AcknowledgedFlag => _ackFlagForEnq;

    /// <summary>The last received NAK flag (used for ENQ polling).</summary>
    public bool NotAcknowledgedFlag => _nakFlagForEnq;

    public bool IsPortOpen => _port.IsOpen;

    // ─── Constructor ──────────────────────────────────────────────────────────
    public DataLink(ISerialPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _port.BytesReceived += OnBytesReceived;
    }

    // ─── Public Methods ───────────────────────────────────────────────────────
    public void Open() => _port.Open();
    public void Close() => _port.Close();

    /// <summary>
    /// Sends an application PDU (without DLE stuffing, without checksum).
    /// Performs DLE stuffing, adds checksum, wraps with DLE STX/ETX,
    /// sends, then waits for ACK/NAK if waitForAck = true.
    /// </summary>
    /// <returns>0 if ACK received, -2 if NAK, -3 if timeout, -6 if write error.</returns>
    public int SendFrame(byte[] pdu, bool waitForAck = true)
    {
        if (pdu == null || pdu.Length == 0)
            return -7;

        // ── 1. DLE stuffing ────────────────────────────────────────────────
        byte[] stuffed = ApplyDleStuffing(pdu);

        // ── 2. Calculate checksum ─────────────────────────────────────────────
        ushort checksum = MessageDecoder.CalculateChecksum(pdu, _checksumType);
        int csLen = (_checksumType == CheckSumOptions.Crc) ? 2 : 1;

        // ── 3. Build frame: DLE STX + stuffed + DLE ETX + checksum ────────
        byte[] frame = new byte[2 + stuffed.Length + 2 + csLen];
        int idx = 0;
        frame[idx++] = DLE;
        frame[idx++] = STX;
        Array.Copy(stuffed, 0, frame, idx, stuffed.Length);
        idx += stuffed.Length;
        frame[idx++] = DLE;
        frame[idx++] = ETX;
        frame[idx++] = (byte)(checksum & 0xFF);
        if (csLen == 2)
            frame[idx++] = (byte)((checksum >> 8) & 0xFF);

        // ── 4. Send with retry and ACK/NAK polling ──────────────────────────
        int retries = 0;
        const int maxRetries = 2;

        while (retries < maxRetries)
        {
            lock (_ackLock)
            {
                _ackReceived = false;
                _nakReceived = false;
            }

            try
            {
                _port.Write(frame, 0, frame.Length);
            }
            catch
            {
                return -6; // cannot write
            }

            // Original SleepDelay: after write, if >0 then Thread.Sleep
            if (_sleepDelay > 0)
                System.Threading.Thread.Sleep(_sleepDelay);

            if (!waitForAck)
                return 0; // no ACK needed (DH485)

            // Polling ACK/NAK (like original with AckWaitTicks)
            int waitTicks = 0;
            bool acked = false, nacked = false;
            while (!acked && !nacked && waitTicks < MaxTicks)
            {
                System.Threading.Thread.Sleep(20);
                lock (_ackLock)
                {
                    acked = _ackReceived;
                    nacked = _nakReceived;
                }
                waitTicks++;
            }

            if (acked)
                return 0;
            if (nacked)
            {
                // Backoff: increase SleepDelay (as original)
                if (_sleepDelay < 400) _sleepDelay += 50;
                retries++;
                continue;
            }
            // Timeout
            return -3;
        }
        return -3;
    }

    /// <summary>Sends a link control byte (ACK, NAK, ENQ) already wrapped with DLE.</summary>
    public void SendControl(byte controlByte)
    {
        if (controlByte != ACK && controlByte != NAK && controlByte != ENQ)
            throw new ArgumentException("Invalid control byte", nameof(controlByte));
        try
        {
            _port.Write(new byte[] { DLE, controlByte }, 0, 2);
        }
        catch { /* ignored, upper layer will detect timeout */ }
    }

    /// <summary>Resets the ACK/NAK flags used for ENQ polling.</summary>
    public void ResetAckNakFlags()
    {
        lock (_ackLock)
        {
            _ackFlagForEnq = false;
            _nakFlagForEnq = false;
        }
    }

    // ─── Private Helpers (DLE Stuffing) ──────────────────────────────────────
    private static byte[] ApplyDleStuffing(byte[] payload)
    {
        var list = new List<byte>(payload.Length * 2);
        foreach (byte b in payload)
        {
            list.Add(b);
            if (b == DLE)
                list.Add(DLE);
        }
        return list.ToArray();
    }

    private static byte[] RemoveDleStuffing(byte[] stuffed)
    {
        var list = new List<byte>(stuffed.Length);
        for (int i = 0; i < stuffed.Length; i++)
        {
            if (stuffed[i] == DLE && i + 1 < stuffed.Length && stuffed[i + 1] == DLE)
            {
                list.Add(DLE);
                i++;
            }
            else
                list.Add(stuffed[i]);
        }
        return list.ToArray();
    }

    // ─── Serial Receive Processing (State Machine) ───────────────────────────
    private void OnBytesReceived(object? sender, byte[] chunk)
    {
        if (chunk == null || chunk.Length == 0) return;

        byte[]? pduToDeliver = null;
        bool enqReceived = false;

        lock (_rxLock)
        {
            _rxBuffer.AddRange(chunk);
            if (_rxBuffer.Count > MaxBufferBytes)
            {
                _rxBuffer.Clear();
                _frameStartTime = DateTime.MinValue;
                return;
            }

            bool consumed = true;
            while (consumed)
            {
                consumed = false;
                if (_rxBuffer.Count < 2) break;

                // Synchronization: look for DLE
                if (_rxBuffer[0] != DLE)
                {
                    _rxBuffer.RemoveAt(0);
                    consumed = true;
                    continue;
                }

                byte ctrl = _rxBuffer[1];

                // ── 2-byte link control: ACK, NAK, ENQ ────────────────────
                if (ctrl == ACK || ctrl == NAK || ctrl == ENQ)
                {
                    _rxBuffer.RemoveRange(0, 2);
                    _frameStartTime = DateTime.MinValue;

                    if (ctrl == ACK)
                    {
                        // Set ACK flag (with timer if SleepDelay > 0)
                        if (_sleepDelay > 0)
                            System.Threading.Thread.Sleep(_sleepDelay);
                        lock (_ackLock)
                        {
                            _ackReceived = true;
                            _ackFlagForEnq = true;
                        }
                    }
                    else if (ctrl == NAK)
                    {
                        lock (_ackLock)
                        {
                            _nakReceived = true;
                            _nakFlagForEnq = true;
                        }
                        _lastResponseWasNAK = true;
                    }
                    else if (ctrl == ENQ)
                    {
                        enqReceived = true; // defer invocation
                    }
                    consumed = true;
                    continue;
                }

                // ── Data frame: DLE STX ... ────────────────────────────────
                if (ctrl == STX)
                {
                    if (_frameStartTime == DateTime.MinValue)
                        _frameStartTime = DateTime.UtcNow;

                    if ((DateTime.UtcNow - _frameStartTime).TotalMilliseconds > FrameTimeoutMs)
                    {
                        _rxBuffer.RemoveRange(0, 2);
                        _frameStartTime = DateTime.MinValue;
                        consumed = true;
                        continue;
                    }

                    // Look for DLE ETX, skipping over DLE DLE (stuffed pair) to avoid false identification.
                    // Example: payload contains 0x10 → on wire becomes 10 10.
                    // If the next byte is 03, the sequence 10 10 03 should be read as stuffed-DLE followed by ETX — not DLE ETX at position 10 (which is part of the pair).
                    int etxIndex = -1;
                    for (int i = 2; i < _rxBuffer.Count - 1; i++)
                    {
                        if (_rxBuffer[i] == DLE)
                        {
                            if (_rxBuffer[i + 1] == DLE)
                            {
                                i++; // skip stuffed pair, continue after the pair
                                continue;
                            }
                            if (_rxBuffer[i + 1] == ETX)
                            {
                                etxIndex = i;
                                break;
                            }
                        }
                    }
                    if (etxIndex == -1) break; // need more bytes

                    int csLen = (_checksumType == CheckSumOptions.Crc) ? 2 : 1;
                    int totalFrameLen = etxIndex + 2 + csLen;
                    if (_rxBuffer.Count < totalFrameLen) break;

                    // Extract frame
                    byte[] frame = new byte[totalFrameLen];
                    _rxBuffer.CopyTo(0, frame, 0, totalFrameLen);
                    _rxBuffer.RemoveRange(0, totalFrameLen);
                    _frameStartTime = DateTime.MinValue;

                    // Unstuff payload (between DLE STX and DLE ETX)
                    int payloadLen = etxIndex - 2;
                    byte[] stuffedPayload = new byte[payloadLen];
                    Array.Copy(frame, 2, stuffedPayload, 0, payloadLen);
                    byte[] pdu = RemoveDleStuffing(stuffedPayload);

                    // Validate checksum
                    bool valid;
                    if (_checksumType == CheckSumOptions.Crc)
                    {
                        ushort calc = MessageDecoder.CalculateChecksum(pdu, CheckSumOptions.Crc);
                        ushort recv = (ushort)(frame[etxIndex + 2] | (frame[etxIndex + 3] << 8));
                        valid = calc == recv;
                    }
                    else
                    {
                        byte calc = (byte)MessageDecoder.CalculateChecksum(pdu, CheckSumOptions.Bcc);
                        byte recv = frame[etxIndex + 2];
                        valid = calc == recv;
                    }

                    // Send ACK/NAK (IMMEDIATELY, without delay)
                    if (valid)
                    {
                        SendControl(ACK);
                        _lastResponseWasNAK = false;
                        pduToDeliver = pdu; // defer invocation
                    }
                    else
                    {
                        SendControl(NAK);
                        _lastResponseWasNAK = true;
                        if (_sleepDelay < 400) _sleepDelay += 50;
                    }
                    consumed = true;
                    continue;
                }

                // DLE followed by unknown byte – discard DLE and resync
                _rxBuffer.RemoveAt(0);
                consumed = true;
            }
        }

        // Invoke events outside the lock
        if (enqReceived)
            EnqReceived?.Invoke(this, EventArgs.Empty);
        if (pduToDeliver != null)
            PacketReceived?.Invoke(this, pduToDeliver);
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        _port.BytesReceived -= OnBytesReceived;
        _port?.Dispose();
    }
}
