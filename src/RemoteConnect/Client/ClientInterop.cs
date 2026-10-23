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
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly HookProc _proc;
    private readonly Func<nint> _window;
    private nint _hook;

    /// <summary>(vk, scan, flags) → true when the key was consumed.</summary>
    public Func<ushort, ushort, KeyFlags, bool>? KeyEvent;
    public Action? ToggleFullScreen;
    public Action? ReleaseGrab;
    public bool Enabled { get; set; } = true;

    public KeyboardHook(Func<nint> window)
    {
        _window = window;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private unsafe nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && Enabled && GetForegroundWindow() == _window())
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            if ((k->flags & LLKHF_INJECTED) == 0)
            {
                bool down = (k->flags & LLKHF_UP) == 0;
                if (down && IsDown(0x11) && IsDown(0x12)) // Ctrl+Alt+…
                {
                    if (k->vkCode == 0x0D) { ToggleFullScreen?.Invoke(); return 1; }
                    if (k->vkCode == 0x24) { ReleaseGrab?.Invoke(); return 1; }
                }
                var flags = (down ? KeyFlags.Down : 0) | ((k->flags & LLKHF_EXTENDED) != 0 ? KeyFlags.Extended : 0);
                if (KeyEvent?.Invoke((ushort)k->vkCode, (ushort)k->scanCode, flags) == true) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _hook = 0;
    }
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
