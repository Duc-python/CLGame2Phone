using System.Diagnostics;
using System.Text;

namespace CLGame2Phone.Agent;

internal sealed record CaptureRect(int X, int Y, int Width, int Height, IntPtr Hwnd);

internal static class Emulator
{
    // LDPlayer không cần đọc port từ file conf; adb server thấy instance là emulator-5554.
    public static int ReadAdbPort() => 5555;

    private static int _guestW = 1600;
    private static int _guestH = 720;

    public static void SetGuestSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _guestW = width;
            _guestH = height;
        }
    }

    private static IntPtr _renderHwnd = IntPtr.Zero;

    private static IntPtr FindRenderWindow(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(parent, (h, _) =>
        {
            var sb = new StringBuilder(256);
            Native.GetClassName(h, sb, 256);
            if (sb.ToString() == "RenderWindow")
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static CaptureRect? TryGetRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _renderHwnd = FindRenderWindow(hwnd);

        if (_renderHwnd == IntPtr.Zero || !Native.GetWindowRect(_renderHwnd, out var rc))
        {
            return null;
        }

        int width = rc.Right - rc.Left;
        int height = rc.Bottom - rc.Top;
        if (width < 100 || height < 100)
        {
            return null;
        }

        return new CaptureRect(rc.Left, rc.Top, width & ~1, height & ~1, hwnd);
    }

    public static CaptureRect FindWindow()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("dnplayer"))
            {
                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }

                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Native.SetWindowPos(
                    hwnd,
                    Native.HWND_TOPMOST,
                    0, 0,
                    _guestW + 40,
                    _guestH + 35,
                    Native.SWP_SHOWWINDOW);
                Native.SetForegroundWindow(hwnd);
                Thread.Sleep(800);

                _renderHwnd = FindRenderWindow(hwnd);
                if (_renderHwnd == IntPtr.Zero || !Native.GetWindowRect(_renderHwnd, out var rc))
                {
                    continue;
                }

                int width = rc.Right - rc.Left;
                int height = rc.Bottom - rc.Top;
                if (width < 100 || height < 100)
                {
                    continue;
                }

                return new CaptureRect(rc.Left, rc.Top, width & ~1, height & ~1, hwnd);
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            "Không tìm thấy cửa sổ LDPlayer (dnplayer). Hãy mở LDPlayer.");
    }
}