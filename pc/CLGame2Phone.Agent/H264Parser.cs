namespace CLGame2Phone.Agent;

internal sealed class H264Parser
{
    private readonly List<byte> _buffer = new(1 << 20);

    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _buffer.Add(b);
        }

        var result = new List<byte[]>();

        while (true)
        {
            int start = FindStartCode(0);
            if (start < 0)
            {
                break;
            }

            int nalStart = start + 3;
            int next = FindStartCode(nalStart);
            if (next < 0)
            {
                break;
            }

            int end = next;
            while (end > nalStart && _buffer[end - 1] == 0)
            {
                end--;
            }

            if (end > nalStart)
            {
                var nal = new byte[end - nalStart];
                _buffer.CopyTo(nalStart, nal, 0, nal.Length);
                result.Add(nal);
            }

            _buffer.RemoveRange(0, next);
        }

        if (_buffer.Count > 8 * 1024 * 1024)
        {
            int start = FindStartCode(0);
            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }
            else if (start < 0)
            {
                _buffer.Clear();
            }
        }

        return result;
    }

    private int FindStartCode(int from)
    {
        for (int i = Math.Max(0, from); i + 2 < _buffer.Count; i++)
        {
            if (_buffer[i] == 0 && _buffer[i + 1] == 0 && _buffer[i + 2] == 1)
            {
                return i;
            }
        }

        return -1;
    }
}
