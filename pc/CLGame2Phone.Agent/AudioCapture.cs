using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CLGame2Phone.Agent;

/// <summary>
/// Bắt âm thanh hệ thống (WASAPI loopback), mã hoá Opus 48kHz stereo 20ms.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 960; // 20ms

    private readonly Action<byte[]> _onPacket;
    private readonly Action<string> _log;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffered;
    private WdlResamplingSampleProvider? _resampler;
    private Thread? _thread;
    private volatile bool _running;
    private long _packets;

    public AudioCapture(Action<byte[]> onPacket, Action<string> log)
    {
        _onPacket = onPacket;
        _log = log;
    }

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            var format = _capture.WaveFormat;
            _log($"input {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit {format.Encoding}");

            _buffered = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(300),
                DiscardOnBufferOverflow = true
            };

            _capture.DataAvailable += (_, e) =>
            {
                try
                {
                    _buffered?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
                catch
                {
                }
            };

            var samples = _buffered.ToSampleProvider();
            _resampler = new WdlResamplingSampleProvider(samples, SampleRate);

            _running = true;
            _thread = new Thread(EncodeLoop) { IsBackground = true, Name = "audio" };
            _thread.Start();
            _capture.StartRecording();
            _log("started");
        }
        catch (Exception ex)
        {
            _log("lỗi: " + ex.Message);
        }
    }

    private void EncodeLoop()
    {
        int channels = _resampler!.WaveFormat.Channels;
        var input = new float[FrameSamples * channels];
        var pcm = new short[FrameSamples * 2];

        int blockAlign = _buffered!.WaveFormat.BlockAlign;
        int inputFramesNeeded = (int)Math.Ceiling(
            FrameSamples * (double)_buffered.WaveFormat.SampleRate / SampleRate);
        int neededBytes = inputFramesNeeded * blockAlign;

        while (_running)
        {
            try
            {
                // Chỉ đọc khi có đủ dữ liệu thật, tránh encoder chạy nhanh hơn thời gian thực.
                if (_buffered.BufferedBytes < neededBytes)
                {
                    Thread.Sleep(5);
                    continue;
                }

                int totalFrames = 0;
                while (totalFrames < FrameSamples)
                {
                    int want = (FrameSamples - totalFrames) * channels;
                    int read = _resampler.Read(input, totalFrames * channels, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    totalFrames += read / channels;
                }

                if (totalFrames < FrameSamples)
                {
                    Thread.Sleep(5);
                    continue;
                }

                for (int i = 0; i < FrameSamples; i++)
                {
                    float l = input[i * channels];
                    float r = channels > 1 ? input[i * channels + 1] : l;
                    pcm[i * 2] = (short)Math.Clamp((int)(l * 32767f), -32768, 32767);
                    pcm[i * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), -32768, 32767);
                }

                var packet = new byte[FrameSamples * 2 * 2];
                Buffer.BlockCopy(pcm, 0, packet, 0, packet.Length);
                _onPacket(packet);

                if (++_packets % 250 == 0)
                {
                    _log($"{_packets} packets");
                }
            }
            catch (Exception ex)
            {
                _log("encode: " + ex.Message);
                Thread.Sleep(100);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
        }

        try
        {
            _thread?.Join(500);
        }
        catch
        {
        }

        try
        {
            _capture?.Dispose();
        }
        catch
        {
        }

        _capture = null;
        _buffered = null;
        _resampler = null;
    }
}
