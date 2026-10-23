using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemoteConnect.Core;
using static RemoteConnect.Core.Native;

namespace RemoteConnect.Client;

/// <summary>
/// Grabs the whole keyboard (Win, Alt+Tab, Ctrl+Esc...) while the remote window is in the foreground,
/// the way RDP does. Ctrl+Alt+Home releases the grab; Ctrl+Alt+Enter toggles full screen.
/// The hook lives on its own thread with its own message loop: a low-level hook answered late (the UI thread
/// busy for a moment) lets the key through to the local PC, and Windows silently removes hooks that keep
/// timing out. <see cref="KeyEvent"/>, <see cref="ToggleFullScreen"/> and <see cref="ReleaseGrab"/> run on that thread.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly HookProc _proc;
    private readonly Func<nint> _window;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _hook;
    private uint _threadId;
    // Modifier state as seen by the hook. GetAsyncKeyState can't be used: keys the hook swallows never reach it.
    private bool _lctrl, _rctrl, _lalt, _ralt;
    private uint _swallowUp; // key of a local shortcut: its release must not reach the remote either

    /// <summary>(vk, scan, flags) → true when the key was consumed.</summary>
    public Func<ushort, ushort, KeyFlags, bool>? KeyEvent;
    public Action? ToggleFullScreen;
    public Action? ReleaseGrab;
    public volatile bool Enabled = true;
    /// <summary>Capturing for real (enabled and the hook installed).</summary>
    public bool Active => Enabled && _hook != 0;

    public KeyboardHook(Func<nint> window)
    {
        _window = window;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "KeyboardHook", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == 0) Log.Write($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0) DispatchMessageW(ref msg);
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    private unsafe nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            // Our own host's injections (client and host on one PC): never loop them back.
            if ((k->flags & LLKHF_INJECTED) != 0 && k->dwExtraInfo == RemoteConnectInputTag) return CallNextHookEx(_hook, nCode, wParam, lParam);
            bool down = (k->flags & LLKHF_UP) == 0;
            switch (k->vkCode)
            {
                case 0xA2: _lctrl = down; break;
                case 0xA3: _rctrl = down; break;
                case 0xA4: _lalt = down; break;
                case 0xA5: _ralt = down; break;
            }
            if (Enabled && GetForegroundWindow() == _window())
            {
                // AltGr comes as a fake left Ctrl (scan code 0x21D) followed by right Alt; the remote layout makes its
                // own fake Ctrl from the right Alt, so this one is swallowed and not sent.
                if ((k->scanCode & 0x200) != 0) return 1;
                if (!down && k->vkCode == _swallowUp) { _swallowUp = 0; return 1; }
                if (down && (_lctrl || _rctrl) && (_lalt || _ralt)) // Ctrl+Alt+…
                {
                    if (k->vkCode == 0x0D) { _swallowUp = k->vkCode; ToggleFullScreen?.Invoke(); return 1; }
                    if (k->vkCode == 0x24) { _swallowUp = k->vkCode; ReleaseGrab?.Invoke(); return 1; }
                }
                var flags = (down ? KeyFlags.Down : 0) | ((k->flags & LLKHF_EXTENDED) != 0 ? KeyFlags.Extended : 0);
                if (KeyEvent?.Invoke((ushort)k->vkCode, (ushort)(k->scanCode & 0xFF), flags) == true) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
        _thread.Join(2000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

public static class CursorFactory
{
    /// <summary>Builds a native cursor (HCURSOR, free with DestroyIcon) from a host pointer shape (kind 1: AND/XOR masks, kind 2: BGRA).</summary>
    public static unsafe nint Create(byte kind, int hotX, int hotY, int w, int h, byte[] data)
    {
        if (w <= 0 || h <= 0) return 0;
        nint mask = 0, color = 0;
        try
        {
            if (kind == 1)
            {
                fixed (byte* p = data) mask = CreateBitmap(w, h * 2, 1, 1, p);
            }
            else
            {
                var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                color = CreateDIBSection(0, ref bi, 0, out void* bits, 0, 0);
                if (color == 0) return 0;
                Marshal.Copy(data, 0, (nint)bits, Math.Min(data.Length, w * h * 4));
                mask = CreateBitmap(w, h, 1, 1, null);
            }
            var info = new ICONINFO { fIcon = 0, xHotspot = hotX, yHotspot = hotY, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (mask != 0) DeleteObject(mask);
            if (color != 0) DeleteObject(color);
        }
    }
}

/// <summary>Plays host audio with a small jitter buffer that is trimmed whenever it grows (latency over smoothness).</summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private OpusDecoder? _opus;
    private int _rate, _channels;

    public void Add(int rate, int channels, AudioCodec codec, byte[] data)
    {
        try
        {
            if (codec == AudioCodec.Opus)
            {
                _opus ??= new OpusDecoder();
                var pcm = _opus.Decode(data);
                if (pcm is null) return;
                data = pcm;
                rate = OpusEncoder.Rate;
                channels = OpusEncoder.Channels;
            }
            if (_out is null || rate != _rate || channels != _channels)
            {
                StopOutput();
                _rate = rate; _channels = channels;
                _buffer = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(600),
                    DiscardOnBufferOverflow = true,
                };
                _out = new WasapiOut(AudioClientShareMode.Shared, true, 40);
                _out.Init(_buffer);
                _out.Play();
            }
            if (_buffer!.BufferedDuration > TimeSpan.FromMilliseconds(180)) _buffer.ClearBuffer();
            _buffer.AddSamples(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Error("audio playback", ex);
            Dispose();
        }
    }

    private void StopOutput()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _buffer = null;
    }

    public void Dispose()
    {
        StopOutput();
        _opus?.Dispose();
        _opus = null;
    }
}
