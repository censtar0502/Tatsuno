using System.IO.Ports;

namespace Tatsuno.Transport.Serial;

public sealed class SerialPortSettings
{
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 19200;
    public Parity Parity { get; init; } = Parity.Even;
    public int DataBits { get; init; } = 8;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake Handshake { get; init; } = Handshake.None;

    public int ReadTimeoutMs { get; init; } = 50;
    public int WriteTimeoutMs { get; init; } = 500;
}
