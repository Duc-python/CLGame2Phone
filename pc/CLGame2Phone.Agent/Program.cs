using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CLGame2Phone.Agent;

internal static class Program
{
    private const int Port = 27183;
    private const int StreamWidth = 1280;
    private const int StreamHeight = 576;
    private const int StreamFps = 60;

    private static volatile bool _running = true;
    private static readonly object SendLock = new();
    private static Stream? _client;
    private static FfmpegCapture? _capture;
    private static InputInjector? _input;
    private static volatile CaptureRect? _rect;
    private static volatile bool _captureDirty;
    private static long _touchCount;
    private static byte[]? _sps;
    private static byte[]? _pps;
    private static long _pts;
    private static long _frames;
    private static long _bytes;
    private static DateTime _lastStats = DateTime.UtcNow;
    private static string _phone = "";
    private static string _emulator = "";
    private static int _androidW = 1600;
    private static int _androidH = 900;
    private static TouchForwarder? _touch;
    private static Process? _touchProcess;
    private static AudioCapture? _audio;

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.EnableDpiAwareness();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        try
        {
            int emulatorPort = Emulator.ReadAdbPort();
            Console.WriteLine($"[LDPlayer] ADB port: {emulatorPort}");
            Adb.ConnectBlueStacks(emulatorPort);

            var devices = Adb.Devices();
            Console.WriteLine("[adb] devices:");
            foreach (var device in devices)
            {
                Console.WriteLine($"  {device.Serial}  {device.State}");
            }

            _phone = Adb.FindPhone();
            Console.WriteLine($"[Phone] {_phone}");
            _emulator = Adb.FindEmulator(_phone);
            Console.WriteLine($"[LDPlayer] {_emulator}");

            Adb.Reverse(_phone, Port);
            Console.WriteLine($"[adb] reverse tcp:{Port} -> tcp:{Port}");

            ReadAndroidSize();
            EnsureTouchServer();
            _touch = new TouchForwarder(27184);
            _touch.EnsureConnected();

            var rect = Emulator.FindWindow();
            _rect = rect;
            Console.WriteLine($"[Capture] client {rect.Width}x{rect.Height} at {rect.X},{rect.Y} (hwnd 0x{rect.Hwnd.ToInt64():X})");
            _input = new InputInjector(rect);

            var watcher = new Thread(WatchWindow) { IsBackground = true };
            watcher.Start();

            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            Console.WriteLine($"[Server] listening 127.0.0.1:{Port}");
            Console.WriteLine("Mở app CLGame2Phone trên điện thoại. Ctrl+C để thoát.");

            while (_running)
            {
                using var client = listener.AcceptTcpClient();
                client.NoDelay = true;
                Console.WriteLine("[Phone] connected");

                _client = client.GetStream();
                _sps = null;
                _pps = null;
                _pts = 0;
                _frames = 0;
                _bytes = 0;
                _lastStats = DateTime.UtcNow;

                SendConfig();
                StartAudio();
                StartCapture(_rect!);

                using var disconnected = new ManualResetEventSlim(false);
                var reader = new Thread(() =>
                {
                    ReadClient(client);
                    disconnected.Set();
                })
                {
                    IsBackground = true
                };
                reader.Start();

                while (_running && !disconnected.Wait(200))
                {
                    if (_captureDirty)
                    {
                        _captureDirty = false;
                        var current = _rect;
                        if (current != null)
                        {
                            Console.WriteLine("[Capture] restarting for new window rect");
                            StopCapture();
                            StartCapture(current);
                        }
                    }

                    PrintStats();
                }

                StopAudio();
                StopCapture();
                _client = null;
                Console.WriteLine("[Phone] disconnected");
                PrintStats();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Fatal] " + ex.Message);
            Console.Error.WriteLine(ex);
        }
        finally
        {
            StopAudio();
            StopCapture();
            _touch?.Dispose();

            if (_touchProcess != null)
            {
                try
                {
                    _touchProcess.Kill(true);
                }
                catch
                {
                }

                _touchProcess = null;
            }

            if (!string.IsNullOrEmpty(_phone))
            {
                Adb.RemoveReverse(_phone, Port);
            }
        }
    }

    private static void ReadAndroidSize()
    {
        try
        {
            string output = Adb.Run("-s", _emulator, "shell", "wm", "size");
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)x(\d+)");
            if (match.Success)
            {
                _androidW = int.Parse(match.Groups[1].Value);
                _androidH = int.Parse(match.Groups[2].Value);
            }

            Emulator.SetGuestSize(_androidW, _androidH);
            Console.WriteLine($"[LDPlayer] Android screen {_androidW}x{_androidH}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LDPlayer] không đọc được wm size: " + ex.Message);
        }
    }

    private static void EnsureTouchServer()
    {
        // Luôn khởi động lại để tránh trạng thái cũ (CanConnect không đáng tin trên LDPlayer).
        try
        {
            Adb.Run("-s", _emulator, "shell", "su -c 'pkill -f TouchServer'");
        }
        catch
        {
        }
        Thread.Sleep(300);

        string jar = Path.Combine(AppContext.BaseDirectory, "touchserver.jar");
        if (!File.Exists(jar))
        {
            Console.WriteLine("[touch] thiếu touchserver.jar: " + jar);
            return;
        }

        Console.WriteLine("[touch] đang cài TouchServer...");
        Adb.Run("-s", _emulator, "push", jar, "/data/local/tmp/touchserver.jar");
        Adb.Run("-s", _emulator, "forward", "tcp:27184", "tcp:27184");

        // Chạy TouchServer với quyền root để process không bị LDPlayer kill.
        // Spoof thêm property để FF không nhận là giả lập/máy tính.
        string[] spoofs = new[]
        {
            "setprop ro.product.device houji",
            "setprop ro.product.name houji",
            "setprop ro.build.fingerprint Xiaomi/houji/houji:14/UKQ1.230804.001/V816.0.5.0.UMQCNXM:user/release-keys",
            "setprop ro.hardware qcom",
            "setprop ro.board.platform taro",
            "setprop ro.product.cpu.abi arm64-v8a",
        };
        foreach (var s in spoofs)
        {
            Adb.Run("-s", _emulator, "shell", $"su -c '{s}'");
        }

        string script = Path.Combine(AppContext.BaseDirectory, "startts.sh");
        if (File.Exists(script))
        {
            Adb.Run("-s", _emulator, "push", script, "/data/local/tmp/startts.sh");
            Adb.Run("-s", _emulator, "shell",
                "su -c 'chmod 755 /data/local/tmp/startts.sh'");
        }
        Adb.Run("-s", _emulator, "shell",
            "su -c 'sh /data/local/tmp/startts.sh </dev/null >/data/local/tmp/ts.log 2>&1 &'");

        _touchProcess = null;

        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            if (CanConnect(27184))
            {
                Console.WriteLine("[touch] TouchServer sẵn sàng");
                return;
            }
        }

        Console.WriteLine("[touch] TouchServer không khởi động được");
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SendConfig()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), StreamWidth);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), StreamHeight);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), StreamFps);
        Send(Protocol.PktConfig, payload);
    }

    private static void StartAudio()
    {
        StopAudio();
        _audio = new AudioCapture(
            packet => Send(Protocol.PktAudio, packet),
            message => Console.WriteLine("[audio] " + message));
        _audio.Start();
    }

    private static void StopAudio()
    {
        var audio = _audio;
        _audio = null;
        audio?.Dispose();
    }

    private static void StartCapture(CaptureRect rect)
    {
        _capture = new FfmpegCapture(rect, OnNal, message => Console.WriteLine("[ffmpeg] " + message));
        _capture.Start();
    }

    private static void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        capture?.Dispose();
    }

    private static void OnNal(byte[] nal)
    {
        if (nal.Length == 0)
        {
            return;
        }

        int type = nal[0] & 0x1F;
        if (type == 7)
        {
            _sps = nal;
        }
        else if (type == 8)
        {
            _pps = nal;
        }

        if (type != 7 && type != 8 && type != 5 && type != 1)
        {
            return;
        }

        long pts = _pts;
        if (type == 1 || type == 5)
        {
            _pts += 1_000_000L / StreamFps;
        }

        var payload = new byte[9 + nal.Length];
        payload[0] = (byte)type;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), pts);
        Buffer.BlockCopy(nal, 0, payload, 9, nal.Length);
        Send(Protocol.PktVideo, payload);

        if (type == 1 || type == 5)
        {
            Interlocked.Increment(ref _frames);
        }
    }

    private static void Send(byte type, byte[] payload)
    {
        var stream = _client;
        if (stream == null)
        {
            return;
        }

        try
        {
            lock (SendLock)
            {
                Protocol.Write(stream, type, payload);
                stream.Flush();
                Interlocked.Add(ref _bytes, payload.Length + 5);
            }
        }
        catch
        {
            _client = null;
        }
    }

    private static void ReadClient(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (_running)
            {
                var packet = Protocol.Read(stream);
                if (packet == null)
                {
                    break;
                }

                var (type, payload) = packet.Value;
                if (type == Protocol.PktTouch)
                {
                    if (payload.Length >= 3)
                    {
                        byte action = payload[0];
                        int count = payload[1] & 0xFF;
                        byte changed = payload[2];

                        if (count >= 1 && payload.Length >= 3 + count * 9)
                        {
                            var ids = new byte[count];
                            var xs = new float[count];
                            var ys = new float[count];
                            int offset = 3;
                            for (int i = 0; i < count; i++)
                            {
                                ids[i] = payload[offset];
                                xs[i] = ReadFloat(payload, offset + 1) * _androidW;
                                ys[i] = ReadFloat(payload, offset + 5) * _androidH;
                                offset += 9;
                            }

                            if (Interlocked.Increment(ref _touchCount) <= 8)
                            {
                                Console.WriteLine(
                                    $"[touch] action={action} n={count} changed={changed} x={xs[0]:F0} y={ys[0]:F0}");
                            }

                            var touch = _touch;
                            if (touch == null || !touch.EnsureConnected())
                            {
                                EnsureTouchServer();
                                touch = _touch;
                            }

                            touch?.Send(action, changed, ids, xs, ys);
                        }
                    }
                }
                else if (type == Protocol.PktKey)
                {
                    if (payload.Length >= 5)
                    {
                        bool down = payload[0] != 0;
                        int vk = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1));
                        _input?.Key(vk, down);
                    }
                }
                else if (type == Protocol.PktMouse)
                {
                    if (payload.Length >= 9)
                    {
                        byte action = payload[0];
                        float x = ReadFloat(payload, 1);
                        float y = ReadFloat(payload, 5);
                        switch (action)
                        {
                            case 0:
                                _input?.MouseMove(x, y);
                                break;
                            case 1:
                                _input?.MouseMove(x, y);
                                _input?.MouseButton(false, true);
                                break;
                            case 2:
                                _input?.MouseButton(false, false);
                                break;
                            case 3:
                                _input?.MouseMove(x, y);
                                _input?.MouseButton(true, true);
                                break;
                            case 4:
                                _input?.MouseButton(true, false);
                                break;
                            case 5:
                                _input?.MouseMoveRelative((int)x, (int)y);
                                break;
                        }
                    }
                }
                else if (type == Protocol.PktPing)
                {
                    Send(Protocol.PktPong, payload);
                }
            }
        }
        catch
        {
        }
    }

    private static void WatchWindow()
    {
        while (_running)
        {
            try
            {
                Thread.Sleep(500);
                var current = _rect;
                if (current == null)
                {
                    continue;
                }

                var now = Emulator.TryGetRect(current.Hwnd);
                if (now == null)
                {
                    continue;
                }

                if (now.X != current.X ||
                    now.Y != current.Y ||
                    now.Width != current.Width ||
                    now.Height != current.Height)
                {
                    Console.WriteLine(
                        $"[Capture] window changed -> {now.Width}x{now.Height} at {now.X},{now.Y}");
                    _rect = now;
                    _input?.SetRect(now);
                    ReadAndroidSize();
                    _captureDirty = true;
                }
            }
            catch
            {
            }
        }
    }

    private static float ReadFloat(byte[] buffer, int offset)
    {
        int bits = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset));
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void PrintStats()
    {
        var now = DateTime.UtcNow;
        double seconds = (now - _lastStats).TotalSeconds;
        if (seconds < 1.0)
        {
            return;
        }

        long frames = Interlocked.Exchange(ref _frames, 0);
        long bytes = Interlocked.Exchange(ref _bytes, 0);
        Console.WriteLine($"[stats] {frames / seconds:F1} fps, {bytes * 8 / seconds / 1_000_000:F1} Mbps");
        _lastStats = now;
    }
}
