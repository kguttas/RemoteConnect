using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace RemoteConnect.Core;

public static unsafe class FFmpegSetup
{
    private static bool _done;
    private static readonly Lock Gate = new();

    public static void Init()
    {
        lock (Gate)
        {
            if (_done) return;
            var nested = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            if (File.Exists(Path.Combine(nested, "avcodec-61.dll"))) ffmpeg.RootPath = nested;
            else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "avcodec-61.dll"))) ffmpeg.RootPath = AppContext.BaseDirectory;
            else
            {
                // Single-file build: the DLLs live in the bundle's extraction folder. Load them through the
                // runtime's probing; FFmpeg.AutoGen's later LoadLibrary("x.dll") then finds them already loaded.
                foreach (var lib in new[] { "avutil-59", "swresample-5", "swscale-8", "avcodec-61" })
                    NativeLibrary.Load(lib, typeof(FFmpegSetup).Assembly, DllImportSearchPath.AssemblyDirectory);
                ffmpeg.RootPath = "";
            }
            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(Environment.GetEnvironmentVariable("REMOTECONNECT_FFLOG") is null ? ffmpeg.AV_LOG_ERROR : ffmpeg.AV_LOG_VERBOSE);
            ffmpeg.av_log_set_callback(LogCallback);
            _done = true;
        }
    }

    // Kept in a field: the native side holds on to the function pointer.
    private static readonly av_log_set_callback_callback LogCallback = (avcl, level, fmt, vl) =>
    {
        if (level > ffmpeg.av_log_get_level()) return;
        var buf = stackalloc byte[1024];
        int prefix = 1;
        ffmpeg.av_log_format_line(avcl, level, fmt, vl, buf, 1024, &prefix);
        var line = Marshal.PtrToStringUTF8((nint)buf)?.TrimEnd();
        if (!string.IsNullOrEmpty(line)) Log.Write("ffmpeg: " + line);
    };

    public static string ErrorText(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return new string((sbyte*)buf);
    }
}
