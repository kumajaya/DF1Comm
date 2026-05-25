namespace DF1Comm.Core;

/// <summary>
/// Abstraction for serial port operations to enable unit testing.
/// </summary>
public interface ISerialPort : IDisposable
{
    event EventHandler<byte[]>? BytesReceived;
    bool IsOpen { get; }
    void Open();
    void Close();
    void Write(byte[] buffer, int offset, int count);
    bool RtsEnable { get; set; }
    bool DtrEnable { get; set; }
}
