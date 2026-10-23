using RemoteConnect.Core;
using static RemoteConnect.Core.Native;

namespace RemoteConnect.Host;

/// <summary>
/// Replays client input with SendInput. Keys go by scan code so layouts and games behave.
/// Everything runs on one dedicated thread that follows the input desktop: SendInput silently does nothing
/// unless the calling thread is on the desktop receiving input (lock screen, sign-in, UAC are other desktops).
/// </summary>
public sealed unsafe class InputInjector : IDisposable
{
    private readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _down = new();
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _buttonsDown;

    public InputInjector()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        using var desktop = new SessionInterop.InputDesktopFollower();
        long lastSync = 0;
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            // OpenInputDesktop is cheap, but mouse moves come at 1 kHz: re-check at most every 100 ms.
            if (Environment.TickCount64 - lastSync > 100) { desktop.Sync(); lastSync = Environment.TickCount64; }
            try { action(); }
            catch (Exception ex) { Log.Error("input", ex); }
        }
    }

    private void Post(Action a)
    {
        try { _queue.Add(a); } catch (InvalidOperationException) { } // disposed
    }

    public void MoveTo(MonitorInfo m, float nx, float ny) => Post(() => DoMoveTo(m, nx, ny));
    public void Button(MouseButtonId button, bool down) => Post(() => DoButton(button, down));
    public void Wheel(int delta, bool horizontal) => Post(() => DoWheel(delta, horizontal));
    public void Key(ushort vk, ushort scan, KeyFlags flags) => Post(() => DoKey(vk, scan, flags));
    public void Chord(params ushort[] vks) => Post(() => DoChord(vks));
    public void ReleaseAll() => Post(DoReleaseAll);

    /// <summary>Releases everything still pressed, then stops the input thread.</summary>
    public void Dispose()
    {
        ReleaseAll();
        _queue.CompleteAdding();
        _thread.Join(2000);
    }

    private void DoMoveTo(MonitorInfo m, float nx, float ny)
    {
        int px = m.Left + (int)Math.Round(Math.Clamp(nx, 0f, 1f) * (m.Width - 1));
        int py = m.Top + (int)Math.Round(Math.Clamp(ny, 0f, 1f) * (m.Height - 1));
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var input = new INPUT { type = INPUT_MOUSE };
        // Absolute coordinates are 0..65535 across the whole virtual desktop; +0.5 keeps us on the target pixel.
        input.mi.dx = (int)(((px - vx) + 0.5) * 65536.0 / vw);
        input.mi.dy = (int)(((py - vy) + 0.5) * 65536.0 / vh);
        input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        Send(input);
    }

    private void DoButton(MouseButtonId button, bool down)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.mi.dwFlags = button switch
        {
            MouseButtonId.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButtonId.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButtonId.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
        };
        if (button is MouseButtonId.X1 or MouseButtonId.X2) input.mi.mouseData = button == MouseButtonId.X1 ? 1u : 2u;
        int bit = 1 << (int)button;
        _buttonsDown = down ? _buttonsDown | bit : _buttonsDown & ~bit;
        Send(input);
    }

    private static void DoWheel(int delta, bool horizontal)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        input.mi.mouseData = unchecked((uint)delta);
        Send(input);
    }

    private void DoKey(ushort vk, ushort scan, KeyFlags flags)
    {
        bool down = flags.HasFlag(KeyFlags.Down);
        bool ext = flags.HasFlag(KeyFlags.Extended);
        var input = new INPUT { type = INPUT_KEYBOARD };
        if (scan != 0)
        {
            input.ki.wScan = scan;
            input.ki.dwFlags = KEYEVENTF_SCANCODE;
        }
        else
        {
            input.ki.wVk = vk;
        }
        if (ext) input.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) input.ki.dwFlags |= KEYEVENTF_KEYUP;
        if (down) _down.Add((vk, scan, ext)); else _down.Remove((vk, scan, ext));
        Send(input);
    }

    /// <summary>Taps a virtual-key chord (e.g. Ctrl+Shift+Esc).</summary>
    private void DoChord(ushort[] vks)
    {
        foreach (var vk in vks) DoKey(vk, 0, KeyFlags.Down);
        for (int i = vks.Length - 1; i >= 0; i--) DoKey(vks[i], 0, 0);
    }

    /// <summary>Releases every key and button we pressed, so a dropped connection never leaves Ctrl held.</summary>
    private void DoReleaseAll()
    {
        foreach (var (vk, scan, ext) in _down.ToList()) DoKey(vk, scan, ext ? KeyFlags.Extended : 0);
        _down.Clear();
        for (int b = 0; b < 5; b++)
            if ((_buttonsDown & (1 << b)) != 0) DoButton((MouseButtonId)b, false);
    }

    private static void Send(INPUT input)
    {
        if (input.type == INPUT_KEYBOARD) input.ki.dwExtraInfo = RemoteConnectInputTag;
        else input.mi.dwExtraInfo = RemoteConnectInputTag;
        SendInput(1, &input, sizeof(INPUT));
    }
}
