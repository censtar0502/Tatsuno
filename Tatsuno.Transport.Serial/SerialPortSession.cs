using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace Tatsuno.Transport.Serial;

public sealed class SerialPortSession : IDisposable
{
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _rxTask;
    // Protects SerialPort.Write() which is not thread-safe (called from poll task + dispatcher)
    private readonly object _sendLock = new();

    public event Action<ReadOnlyMemory<byte>>? BytesReceived;
    public event Action<Exception>? ReceiveFault;

    public bool IsOpen => _port?.IsOpen == true;

    public void Open(SerialPortSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (IsOpen) throw new InvalidOperationException("Port already open.");

        // BUG FIX: use actual settings instead of hardcoded 19200/Even/8/One
        _port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            ReadTimeout = settings.ReadTimeoutMs,
            WriteTimeout = settings.WriteTimeoutMs,
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n"
        };

        _port.Open();

        System.Diagnostics.Debug.WriteLine(
            $"SERIAL OPEN: {_port.PortName} {_port.BaudRate} {_port.DataBits} {_port.Parity} {_port.StopBits} {_port.Handshake}");

        _cts = new CancellationTokenSource();
        _rxTask = Task.Run(() => RxLoop(_port, _cts.Token), _cts.Token);
    }

    public void Close()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { /* ignore */ }

        try
        {
            _rxTask?.Wait(500);
        }
        catch { /* ignore */ }

        try
        {
            _port?.Close();
        }
        catch { /* ignore */ }

        _rxTask = null;
        _cts?.Dispose();
        _cts = null;

        _port?.Dispose();
        _port = null;
    }

    public void Send(ReadOnlySpan<byte> bytes)
    {
        SerialPort? port = _port;
        if (port is null || !port.IsOpen)
            throw new InvalidOperationException("Port is not open.");

        // BUG FIX: lock to prevent concurrent writes from poll task and dispatcher thread
        byte[] buf = bytes.ToArray();
        lock (_sendLock)
        {
            port.Write(buf, 0, buf.Length);
        }
    }

    private void RxLoop(SerialPort port, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int n = 0;
                try
                {
                    n = port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    n = 0;
                }

                if (n > 0)
                {
                    byte[] data = new byte[n];
                    Buffer.BlockCopy(buffer, 0, data, 0, n);
                    BytesReceived?.Invoke(data);
                }
            }
            catch (Exception ex)
            {
                ReceiveFault?.Invoke(ex);
                Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        Close();
    }
}
