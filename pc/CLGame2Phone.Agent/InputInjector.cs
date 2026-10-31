using System.Runtime.InteropServices;

namespace CLGame2Phone.Agent;

internal sealed class InputInjector
{
    private volatile CaptureRect _rect;
    private bool _down;

    public InputInjector(CaptureRect rect)
    {
        _rect = rect;
    }

    public void SetRect(CaptureRect rect)
    {
        _rect = rect;
    }

    public void Key(int vk, bool down)
    {
        var rect = _rect;
        Native.SetForegroundWindow(rect.Hwnd);

        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD
        };
        input.U.ki.wVk = (ushort)vk;
        input.U.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
        input.U.ki.dwFlags = down ? 0u : Native.KEYEVENTF_KEYUP;

        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    public void MouseMove(float nx, float ny)
    {
        var rect = _rect;
        Native.SetForegroundWindow(rect.Hwnd);
        int x = rect.X + (int)(Math.Clamp(nx, 0f, 1f) * rect.Width);
        int y = rect.Y + (int)(Math.Clamp(ny, 0f, 1f) * rect.Height);
        Native.SetCursorPos(x, y);
    }

    public void MouseButton(bool right, bool down)
    {
        var rect = _rect;
        Native.SetForegroundWindow(rect.Hwnd);
        uint flag = right
            ? (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP)
            : (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP);
        Native.mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
    }

    public void MouseMoveRelative(int dx, int dy)
    {
        Native.mouse_event(Native.MOUSEEVENTF_MOVE, (uint)dx, (uint)dy, 0, UIntPtr.Zero);
    }

    public void Touch(byte action, float nx, float ny)
    {
        var rect = _rect;
        int x = rect.X + (int)(Math.Clamp(nx, 0f, 1f) * rect.Width);
        int y = rect.Y + (int)(Math.Clamp(ny, 0f, 1f) * rect.Height);

        switch (action)
        {
            case 0:
                Native.SetForegroundWindow(rect.Hwnd);
                Native.SetCursorPos(x, y);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                _down = true;
                break;

            case 1:
                Native.SetCursorPos(x, y);
                break;

            case 2:
                Native.SetCursorPos(x, y);
                if (_down)
                {
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }

                _down = false;
                break;

            case 3:
                if (_down)
                {
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }

                _down = false;
                break;
        }
    }
}
