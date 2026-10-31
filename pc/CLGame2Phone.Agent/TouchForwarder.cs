using System.Buffers.Binary;
using System.Net.Sockets;

namespace CLGame2Phone.Agent;

/// <summary>
/// Gửi sự kiện cảm ứng multi-touch sang TouchServer chạy trong BlueStacks.
/// </summary>
internal sealed class TouchForwarder : IDisposable
{
    private readonly int _port;
    private readonly object _lock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private DateTime _lastAttempt = DateTime.MinValue;

    public TouchForwarder(int port)
    {
        _port = port;
    }

    public bool Connected
    {
        get
        {
            lock (_lock)
            {
                return _stream != null;
            }
        }
    }

    public bool EnsureConnected()
    {
        lock (_lock)
        {
            if (_stream != null)
            {
                return true;
            }

            if ((DateTime.UtcNow - _lastAttempt).TotalMilliseconds < 800)
            {
                return false;
            }

            _lastAttempt = DateTime.UtcNow;

            try
            {
                var client = new TcpClient
                {
                    NoDelay = true
                };
                client.Connect("127.0.0.1", _port);
                _client = client;
                _stream = client.GetStream();
                Console.WriteLine("[touch] connected to TouchServer");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Send(byte action, byte changed, byte[] ids, float[] xs, float[] ys)
    {
        lock (_lock)
        {
            var stream = _stream;
            if (stream == null)
            {
                return;
            }

            int count = ids.Length;
            int length = 3 + count * 9;

            try
            {
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, length);

                Span<byte> payload = stackalloc byte[length];
                payload[0] = action;
                payload[1] = (byte)count;
                payload[2] = changed;

                int offset = 3;
                for (int i = 0; i < count; i++)
                {
                    payload[offset++] = ids[i];
                    BinaryPrimitives.WriteSingleBigEndian(payload.Slice(offset), xs[i]);
                    offset += 4;
                    BinaryPrimitives.WriteSingleBigEndian(payload.Slice(offset), ys[i]);
                    offset += 4;
                }

                stream.Write(header);
                stream.Write(payload);
                stream.Flush();
            }
            catch
            {
                Close();
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
            }

            try
            {
                _client?.Dispose();
            }
            catch
            {
            }

            _stream = null;
            _client = null;
        }
    }

    public void Dispose()
    {
        Close();
    }
}
