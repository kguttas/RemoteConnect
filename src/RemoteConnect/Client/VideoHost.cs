using System.Runtime.InteropServices;
using System.Windows.Interop;
using RemoteConnect.Core;

namespace RemoteConnect.Client;

/// <summary>
/// A plain Win32 child window for the remote picture: the decode thread presents into it with a DXGI
/// flip-model swap chain, so frames reach the screen without going through WPF's composition. It also
/// is the input surface (mouse, wheel, dropped files) and shows the remote cursor. Coordinates are
/// physical pixels of its client area. Keyboard focus stays with the WPF window.
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C,
        WM_MOUSEHWHEEL = 0x020E, WM_CAPTURECHANGED = 0x0215, WM_DROPFILES = 0x0233, WM_MOUSELEAVE = 0x02A3;
    private const int HTCLIENT = 1;
    private const int MK_BUTTONS = 0x0001 | 0x0002 | 0x0010 | 0x0020 | 0x0040;
    private const string ClassName = "RemoteConnect.Video";

    private static readonly Lock ClassGate = new();
    private static bool _registered;
    private static readonly nint DefProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");
    private nint _cursor = LoadCursorW(0, 32512); // IDC_ARROW
    private bool _tracking;

    public nint Hwnd { get; private set; }
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }
    public bool MouseInside { get; private set; }

    public event Action<int, int>? PixelSizeChanged;
    public event Action<int, int>? MouseMoved;
    public event Action<MouseButtonId, bool, int, int>? MouseButton;
    public event Action<int, bool>? Wheel;
    public event Action<string[]>? FilesDropped;
    public event Action? CaptureLost;
    public event Action? PaintRequested;

    /// <summary>The remote pointer shape (HCURSOR, 0 = hidden). The caller owns the handle.</summary>
    public nint CursorHandle
    {
        get => _cursor;
        set
        {
            _cursor = value;
            if (MouseInside) SetCursor(value);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hinst = GetModuleHandleW(null); // the exe: works for single-file publish too
        lock (ClassGate)
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0, // no CS_DBLCLKS: plain down/up pairs, like the remote expects
                    lpfnWndProc = DefProc, // HwndHost subclasses the window and routes messages to WndProc
                    hInstance = hinst,
                    lpszClassName = ClassName,
                };
                if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already exists */)
                    throw new System.ComponentModel.Win32Exception();
                _registered = true;
            }
        }
        Hwnd = CreateWindowExW(0x10 /* WS_EX_ACCEPTFILES */, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, 0, hinst, 0);
        if (Hwnd == 0) throw new System.ComponentModel.Win32Exception();
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        Hwnd = 0;
    }

    private static int X(nint lParam) => (short)((long)lParam & 0xFFFF);
    private static int Y(nint lParam) => (short)(((long)lParam >> 16) & 0xFFFF);

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SIZE:
                PixelWidth = (int)((long)lParam & 0xFFFF);
                PixelHeight = (int)(((long)lParam >> 16) & 0xFFFF);
                PixelSizeChanged?.Invoke(PixelWidth, PixelHeight);
                break;
            case WM_PAINT:
                ValidateRect(hwnd, 0);
                PaintRequested?.Invoke();
                handled = true;
                return 0;
            case WM_ERASEBKGND:
                handled = true;
                return 1; // the swap chain covers the whole client area
            case WM_SETCURSOR when ((long)lParam & 0xFFFF) == HTCLIENT:
                SetCursor(_cursor);
                handled = true;
                return 1;
            case WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
                    _tracking = TrackMouseEvent(ref tme);
                }
                MouseInside = true;
                MouseMoved?.Invoke(X(lParam), Y(lParam));
                handled = true;
                return 0;
            case WM_MOUSELEAVE:
                _tracking = false;
                MouseInside = false;
                break;
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN:
            case WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP:
            {
                bool down = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;
                var button = msg switch
                {
                    WM_LBUTTONDOWN or WM_LBUTTONUP => MouseButtonId.Left,
                    WM_RBUTTONDOWN or WM_RBUTTONUP => MouseButtonId.Right,
                    WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButtonId.Middle,
                    _ => (((long)wParam >> 16) & 0xFFFF) == 1 ? MouseButtonId.X1 : MouseButtonId.X2,
                };
                // Keep getting moves and the release while dragging outside the window.
                if (down) SetCapture(hwnd);
                MouseButton?.Invoke(button, down, X(lParam), Y(lParam));
                if (!down && ((long)wParam & MK_BUTTONS) == 0) ReleaseCapture();
                handled = true;
                return msg is WM_XBUTTONDOWN or WM_XBUTTONUP ? 1 : 0;
            }
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
                Wheel?.Invoke((short)(((long)wParam >> 16) & 0xFFFF), msg == WM_MOUSEHWHEEL);
                handled = true;
                return 0;
            case WM_CAPTURECHANGED:
                CaptureLost?.Invoke();
                break;
            case WM_DROPFILES:
            {
                uint count = DragQueryFileW(wParam, 0xFFFFFFFF, null, 0);
                var files = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFileW(wParam, i, null, 0);
                    var buf = new char[len + 1];
                    DragQueryFileW(wParam, i, buf, len + 1);
                    files[i] = new string(buf, 0, (int)len);
                }
                DragFinish(wParam);
                FilesDropped?.Invoke(files);
                handled = true;
                return 0;
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>Is the mouse pointer over this window right now (screen position check)?</summary>
    public bool CursorOver()
    {
        if (Hwnd == 0 || !GetCursorPos(out var pt)) return false;
        return WindowFromPoint(pt) == Hwnd;
    }

    // ---------------- interop ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public int style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public int dwFlags;
        public nint hwndTrack;
        public int dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT pt);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(nint drop, uint index, char[]? file, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(nint drop);
}
