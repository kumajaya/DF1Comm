using System.IO.Ports;

namespace DF1Comm.Core;

/// <summary>
/// Serial port wrapper with robust byte array handling and deadlock prevention.
/// </summary>
public class SerialPortWrapper : ISerialPort
{
    private readonly SerialPort _port;
    private readonly object _sync = new object();

    public event EventHandler<byte[]>? BytesReceived;

    public SerialPortWrapper(string portName, int baudRate, Parity parity)
    {
        _port = new SerialPort(portName, baudRate, parity, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000
        };
        _port.DataReceived += Port_DataReceived;
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            int toRead = _port.BytesToRead;
            if (toRead <= 0) return;

            byte[] buffer = new byte[toRead];
            int read = _port.Read(buffer, 0, toRead);

            if (read > 0)
            {
                // Crop buffer to actual bytes read
                byte[] actualData = new byte[read];
                Buffer.BlockCopy(buffer, 0, actualData, 0, read);

                // Offload event to thread pool to avoid deadlock on Close()
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        BytesReceived?.Invoke(this, actualData);
                    }
                    catch
                    {
                        // Ignore exceptions in event handlers (upper layer handles)
                    }
                });
            }
        }
        catch
        {
            // Ignore serial port read errors; upper layer will timeout
        }
    }

    public void Open()
    {
        lock (_sync)
        {
            if (!_port.IsOpen) _port.Open();
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_port.IsOpen) _port.Close();
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            if (_port.IsOpen)
            {
                _port.Write(buffer, offset, count);
            }
        }
    }

    public bool IsOpen
    {
        get { lock (_sync) { return _port.IsOpen; } }
    }

    public bool RtsEnable
    {
        get { lock (_sync) { return _port.RtsEnable; } }
        set { lock (_sync) { _port.RtsEnable = value; } }
    }

    public bool DtrEnable
    {
        get { lock (_sync) { return _port.DtrEnable; } }
        set { lock (_sync) { _port.DtrEnable = value; } }
    }

    public void Dispose()
    {
        try
        {
            _port.DataReceived -= Port_DataReceived;
            Close();
            _port.Dispose();
        }
        catch { }
    }
}
