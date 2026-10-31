using System.Buffers.Binary;

namespace CLGame2Phone.Agent;

internal static class Protocol
{
    public const byte PktVideo = 1;
    public const byte PktConfig = 2;
    public const byte PktPong = 3;
    public const byte PktAudio = 6;

    public const byte PktTouch = 1;
    public const byte PktPing = 2;
    public const byte PktKey = 4;
    public const byte PktMouse = 5;

    public static void Write(Stream stream, byte type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);
        header[4] = type;
        stream.Write(header);
        stream.Write(payload);
    }

    public static (byte Type, byte[] Payload)? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExact(stream, header))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > 16 * 1024 * 1024)
        {
            return null;
        }

        var buffer = new byte[length];
        if (!ReadExact(stream, buffer))
        {
            return null;
        }

        byte type = buffer[0];
        var payload = new byte[length - 1];
        Buffer.BlockCopy(buffer, 1, payload, 0, payload.Length);
        return (type, payload);
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(offset));
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
