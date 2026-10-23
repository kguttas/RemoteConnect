using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RemoteConnect.Core;

/// <summary>
/// Watches the local clipboard and applies remote clipboard content. Must be created on a WPF dispatcher thread.
/// Content we set ourselves is remembered by hash so it is never echoed back.
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    public const int MaxBytes = 32 * 1024 * 1024;

    private readonly HwndSource _source;
    private ulong _lastHash;

    /// <summary>Raised on the dispatcher thread with (kind, data) when the user copies something locally.</summary>
    public event Action<ClipboardKind, byte[]>? LocalChanged;

    public ClipboardBridge()
    {
        var p = new HwndSourceParameters("RemoteConnectClipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Native.AddClipboardFormatListener(_source.Handle);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            // Let the source app finish writing all formats before reading.
            _source.Dispatcher.BeginInvoke(ReadLocal, System.Windows.Threading.DispatcherPriority.Background);
        }
        return 0;
    }

    private void ReadLocal()
    {
        try
        {
            (ClipboardKind Kind, byte[] Data)? item = null;
            if (Retry(Clipboard.ContainsText))
            {
                var text = Retry(() => Clipboard.GetText(TextDataFormat.UnicodeText));
                if (!string.IsNullOrEmpty(text)) item = (ClipboardKind.Text, Encoding.UTF8.GetBytes(text));
            }
            else if (Retry(Clipboard.ContainsImage))
            {
                var img = Retry(Clipboard.GetImage);
                if (img is not null) item = (ClipboardKind.Png, EncodePng(img));
            }
            if (item is null || item.Value.Data.Length > MaxBytes) return;
            var h = Hash(item.Value.Kind, item.Value.Data);
            if (h == _lastHash) return;
            _lastHash = h;
            LocalChanged?.Invoke(item.Value.Kind, item.Value.Data);
        }
        catch (Exception ex) { Log.Error("clipboard read", ex); }
    }

    public void ApplyRemote(ClipboardKind kind, byte[] data)
    {
        try
        {
            _lastHash = Hash(kind, data);
            if (kind == ClipboardKind.Text)
                Retry(() => { Clipboard.SetText(Encoding.UTF8.GetString(data), TextDataFormat.UnicodeText); return true; });
            else if (kind == ClipboardKind.Png)
            {
                var decoder = new PngBitmapDecoder(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                Retry(() => { Clipboard.SetImage(frame); return true; });
            }
        }
        catch (Exception ex) { Log.Error("clipboard write", ex); }
    }

    private static ulong Hash(ClipboardKind kind, byte[] data) => XxHash64.HashToUInt64(data, (long)kind);

    private static byte[] EncodePng(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static T Retry<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (COMException) when (i < 5) { Thread.Sleep(20); }
        }
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
