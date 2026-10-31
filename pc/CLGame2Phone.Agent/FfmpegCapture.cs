using System.Diagnostics;

namespace CLGame2Phone.Agent;

internal sealed class FfmpegCapture : IDisposable
{
    private readonly CaptureRect _rect;
    private readonly Action<byte[]> _onNal;
    private readonly Action<string> _log;
    private Process? _process;
    private Thread? _reader;
    private volatile bool _running;

    public FfmpegCapture(CaptureRect rect, Action<byte[]> onNal, Action<string> log)
    {
        _rect = rect;
        _onNal = onNal;
        _log = log;
    }

    public void Start()
    {
        _running = true;

        var psi = new ProcessStartInfo(FfmpegPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Arg(params string[] values)
        {
            foreach (var value in values)
            {
                psi.ArgumentList.Add(value);
            }
        }

        Arg("-hide_banner", "-loglevel", "warning", "-nostdin");
        Arg("-fflags", "nobuffer");
        Arg("-f", "lavfi", "-i",
            $"ddagrab=output_idx=0:framerate=60:video_size={_rect.Width}x{_rect.Height}" +
            $":offset_x={_rect.X}:offset_y={_rect.Y}:draw_mouse=0");
        Arg("-vf", "hwdownload,format=bgra,scale=1280:576:flags=fast_bilinear");
        Arg("-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "cbr");
        Arg("-b:v", "5M", "-maxrate", "5M", "-bufsize", "2500k");
        Arg("-g", "30", "-bf", "0", "-zerolatency", "1");
        Arg("-pix_fmt", "yuv420p", "-f", "h264", "pipe:1");

        _process = new Process { StartInfo = psi };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log(e.Data);
            }
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _reader = new Thread(ReadLoop) { IsBackground = true };
        _reader.Start();

        _log($"started pid {_process.Id}, capture {_rect.Width}x{_rect.Height} at {_rect.X},{_rect.Y}");
    }

    private void ReadLoop()
    {
        var parser = new H264Parser();
        var buffer = new byte[64 * 1024];

        try
        {
            var stream = _process!.StandardOutput.BaseStream;
            while (_running)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                foreach (var nal in parser.Feed(buffer.AsSpan(0, read)))
                {
                    _onNal(nal);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                _log("stdout: " + ex.Message);
            }
        }
    }

    private static string FfmpegPath()
    {
        var wingetRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages");

        if (Directory.Exists(wingetRoot))
        {
            foreach (var dir in Directory.GetDirectories(wingetRoot, "Gyan.FFmpeg_*"))
            {
                foreach (var exe in Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories))
                {
                    return exe;
                }
            }
        }

        return "ffmpeg";
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _process?.Kill(true);
        }
        catch
        {
        }

        try
        {
            _reader?.Join(1000);
        }
        catch
        {
        }

        try
        {
            _process?.Dispose();
        }
        catch
        {
        }

        _process = null;
    }
}
