using System.Diagnostics;

namespace CLGame2Phone.Agent;

internal static class Adb
{
    public static string Path { get; } = FindAdb();

    public static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Không chạy được adb");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (stdout + stderr).Trim();
    }

    public static List<(string Serial, string State)> Devices()
    {
        var output = Run("devices");
        var list = new List<(string, string)>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }

            if (parts.Length >= 2)
            {
                list.Add((parts[0], parts[1]));
            }
        }

        return list;
    }

    public static string FindPhone()
    {
        var devices = Devices();

        foreach (var device in devices)
        {
            if (device.State != "device")
            {
                continue;
            }

            if (device.Serial.StartsWith("emulator-", StringComparison.Ordinal))
            {
                continue;
            }

            if (device.Serial.Contains(':'))
            {
                continue;
            }

            return device.Serial;
        }

        throw new InvalidOperationException(
            "Không thấy điện thoại USB ở trạng thái 'device'. Kiểm tra cáp, USB debugging và hộp thoại cho phép PC.\n" +
            string.Join("\n", devices.Select(d => $"  {d.Serial}  {d.State}")));
    }

    public static string FindEmulator(string phoneSerial)
    {
        string? fallback = null;

        foreach (var device in Devices())
        {
            if (device.State != "device" || device.Serial == phoneSerial)
            {
                continue;
            }

            if (device.Serial.StartsWith("emulator-", StringComparison.Ordinal))
            {
                return device.Serial;
            }

            fallback ??= device.Serial;
        }

        if (fallback != null)
        {
            return fallback;
        }

        throw new InvalidOperationException("Không thấy BlueStacks qua ADB. Bật ADB trong BlueStacks Settings > Advanced.");
    }

    public static void ConnectBlueStacks(int port)
    {
        var result = Run("connect", $"127.0.0.1:{port}");
        Console.WriteLine($"[adb] connect 127.0.0.1:{port}: {result}");
    }

    public static void Reverse(string serial, int port)
    {
        var result = Run("-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}");
        if (!string.IsNullOrWhiteSpace(result))
        {
            Console.WriteLine($"[adb] reverse: {result}");
        }
    }

    public static void RemoveReverse(string serial, int port)
    {
        Run("-s", serial, "reverse", "--remove", $"tcp:{port}");
    }

    public static void LaunchApp(string serial)
    {
        Run("-s", serial, "shell", "am", "start", "-n", "com.clgame2phone.client/.MainActivity");
    }

    private static string FindAdb()
    {
        var candidates = new List<string>();
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            candidates.Add(System.IO.Path.Combine(androidHome, "platform-tools", "adb.exe"));
        }
        candidates.Add(@"C:\Android\Sdk\platform-tools\adb.exe");
        candidates.Add(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages\Google.PlatformTools_Microsoft.Winget.Source_8wekyb3d8bbwe\platform-tools\adb.exe"));
        candidates.Add("adb.exe");

        foreach (var candidate in candidates)
        {
            if (candidate == "adb.exe")
            {
                try
                {
                    var psi = new ProcessStartInfo("where.exe", "adb")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null)
                    {
                        continue;
                    }

                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                    if (output.Length > 0)
                    {
                        return output.Split('\n')[0].Trim();
                    }
                }
                catch
                {
                }
            }
            else if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Không tìm thấy adb.exe. Cài Android platform-tools hoặc kiểm tra PATH.");
    }
}
