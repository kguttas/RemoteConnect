using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// Changes the resolution and the Windows scale (100 %, 125 %…) of a monitor for the duration of a session, so a
/// small high-DPI screen (handhelds, tablets) is comfortable to drive from a big one, and puts it back afterwards.
/// Changes are dynamic (not written as the display's saved mode); a marker file lets the next session restore
/// them if the host died before it could.
/// Must run on a thread attached to the input desktop (the capture thread).
/// </summary>
public sealed class DisplayControl : IDisposable
{
    /// <summary>The scale steps Windows uses; the DPI API addresses them relative to the recommended one.</summary>
    private static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    private static string MarkerFile => Path.Combine(Paths.HostDir, "display-restore.json");

    private sealed class Original
    {
        public string Device { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Frequency { get; set; }
        public int Scale { get; set; }
    }

    private Original? _original;
    /// <summary>Testing aid: call Windows even for values that are already set (exercises the APIs without changing the screen).</summary>
    private static readonly bool AlwaysCall = Environment.GetEnvironmentVariable("REMOTECONNECT_DISPLAYTEST") == "1";

    public DisplayInfo Query(string device)
    {
        var info = new DisplayInfo { Device = device };
        if (!Current(device, out var dm)) { info.Error = "No se pudo leer el modo de la pantalla."; return info; }
        info.Width = (int)dm.dmPelsWidth;
        info.Height = (int)dm.dmPelsHeight;
        info.Modes = Modes(device, info.Width >= info.Height).Select(m => new DisplayMode { Width = m.W, Height = m.H }).ToList();
        if (GetScale(device) is { } s)
        {
            info.Scale = s.Current;
            info.RecommendedScale = s.Recommended;
            info.Scales = ScaleSteps.Where(x => x >= s.Min && x <= s.Max).ToList();
        }
        if (_original is { } o && o.Device == device)
        {
            info.Changed = true;
            info.OriginalWidth = o.Width;
            info.OriginalHeight = o.Height;
            info.OriginalScale = o.Scale;
        }
        return info;
    }

    /// <summary>Applies a resolution (0 = keep) and a scale in percent (0 = keep). Returns an error or null.</summary>
    public string? Apply(string device, int width, int height, int scale)
    {
        if (!Current(device, out var dm)) return "No se pudo leer el modo de la pantalla.";
        if (_original is null)
        {
            _original = new Original
            {
                Device = device, Width = (int)dm.dmPelsWidth, Height = (int)dm.dmPelsHeight,
                Frequency = (int)dm.dmDisplayFrequency, Scale = GetScale(device)?.Current ?? 0,
            };
            try { File.WriteAllText(MarkerFile, JsonSerializer.Serialize(_original)); } catch (Exception ex) { Log.Write("display marker: " + ex.Message); }
        }
        string? error = null;
        if (width > 0 && height > 0 && (AlwaysCall || width != dm.dmPelsWidth || height != dm.dmPelsHeight))
        {
            var mode = Modes(device, dm.dmPelsWidth >= dm.dmPelsHeight).FirstOrDefault(m => m.W == width && m.H == height);
            if (mode.W == 0) return $"La pantalla no admite {width}×{height}.";
            error = SetMode(device, width, height, mode.Hz);
        }
        if (error is null && scale > 0 && GetScale(device) is { } s && (AlwaysCall || s.Current != scale))
            error = SetScale(device, scale);
        Log.Write($"Display {device} → {width}x{height} @ {scale} %: {error ?? "ok"}");
        return error;
    }

    /// <summary>Puts the monitor back the way the session found it.</summary>
    public void Restore()
    {
        if (_original is not { } o) return;
        _original = null;
        RestoreFrom(o);
        try { File.Delete(MarkerFile); } catch { }
    }

    /// <summary>A previous session changed the display and never restored it (crash, power loss): do it now.</summary>
    public static void RestorePending()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return;
            var o = JsonSerializer.Deserialize<Original>(File.ReadAllText(MarkerFile));
            File.Delete(MarkerFile);
            if (o is not null) { Log.Write("Restoring the display left changed by an earlier session"); RestoreFrom(o); }
        }
        catch (Exception ex) { Log.Write("display restore: " + ex.Message); }
    }

    private static void RestoreFrom(Original o)
    {
        if (Current(o.Device, out var dm) && (dm.dmPelsWidth != o.Width || dm.dmPelsHeight != o.Height))
            Log.Write($"Display {o.Device} restore {o.Width}x{o.Height}: {SetMode(o.Device, o.Width, o.Height, o.Frequency) ?? "ok"}");
        if (o.Scale > 0 && GetScale(o.Device) is { } s && s.Current != o.Scale)
            Log.Write($"Display {o.Device} restore {o.Scale} %: {SetScale(o.Device, o.Scale) ?? "ok"}");
    }

    public void Dispose() => Restore();

    // ---------------- resolution ----------------

    private static bool Current(string device, out DEVMODE dm)
    {
        dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref dm, 0);
    }

    /// <summary>Distinct resolutions in the current orientation (landscape or portrait), largest first, with their best refresh rate.</summary>
    private static List<(int W, int H, int Hz)> Modes(string device, bool landscape)
    {
        var best = new Dictionary<(int, int), int>();
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettingsEx(device, i, ref dm, 0); i++)
        {
            int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight;
            if (dm.dmBitsPerPel < 24 || Math.Max(w, h) < 1024 || Math.Min(w, h) < 600) continue;
            if ((w >= h) != landscape && w != h) (w, h) = (h, w); // listed in the other orientation
            best[(w, h)] = Math.Max(best.GetValueOrDefault((w, h)), (int)dm.dmDisplayFrequency);
        }
        return best.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderByDescending(m => m.Item1 * m.Item2).ThenByDescending(m => m.Item1).ToList();
    }

    private static string? SetMode(string device, int width, int height, int hz)
    {
        if (!Current(device, out var dm)) return "No se pudo leer el modo de la pantalla.";
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
        if (hz > 1) { dm.dmDisplayFrequency = (uint)hz; dm.dmFields |= DM_DISPLAYFREQUENCY; }
        int r = ChangeDisplaySettingsEx(device, ref dm, 0, 0 /* dynamic: not saved as the display's mode */, 0);
        return r == 0 ? null : $"Windows rechazó {width}×{height} (código {r}).";
    }

    // ---------------- scale (DPI) ----------------
    // DISPLAYCONFIG_DEVICE_INFO_GET/SET_DPI_SCALE (-3 / -4): what Settings → Display → Scale uses. Not in the
    // public headers, but stable since Windows 10 1607 and used by many tools.

    private static (int Min, int Current, int Max, int Recommended)? GetScale(string device)
    {
        if (FindSource(device) is not { } src) return null;
        var g = new DpiGet { Header = new Header { Type = -3, Size = Marshal.SizeOf<DpiGet>(), Adapter = src.Adapter, Id = src.Id } };
        if (DisplayConfigGetDeviceInfo(ref g) != 0) return null;
        int rec = Math.Abs(g.Min);
        int At(int rel) => ScaleSteps[Math.Clamp(rec + rel, 0, ScaleSteps.Length - 1)];
        return (At(g.Min), At(g.Cur), At(g.Max), ScaleSteps[Math.Clamp(rec, 0, ScaleSteps.Length - 1)]);
    }

    private static string? SetScale(string device, int percent)
    {
        if (FindSource(device) is not { } src) return "No se encontró la pantalla para cambiar la escala.";
        var g = new DpiGet { Header = new Header { Type = -3, Size = Marshal.SizeOf<DpiGet>(), Adapter = src.Adapter, Id = src.Id } };
        if (DisplayConfigGetDeviceInfo(ref g) != 0) return "No se pudo leer la escala.";
        int target = Array.IndexOf(ScaleSteps, percent);
        if (target < 0) return $"Escala no válida: {percent} %.";
        int rel = Math.Clamp(target - Math.Abs(g.Min), g.Min, g.Max);
        var set = new DpiSet { Header = new Header { Type = -4, Size = Marshal.SizeOf<DpiSet>(), Adapter = src.Adapter, Id = src.Id }, Scale = rel };
        // The scale is a per-user setting: as SYSTEM, apply it as the signed-in user.
        int r;
        using (var token = SessionInterop.IsSystem ? SessionInterop.SessionUserToken() : null)
            r = token is null ? DisplayConfigSetDeviceInfo(ref set)
                : System.Security.Principal.WindowsIdentity.RunImpersonated(token, () => DisplayConfigSetDeviceInfo(ref set));
        return r == 0 ? null : $"Windows rechazó la escala {percent} % (código {r}).";
    }

    private static (Luid Adapter, uint Id)? FindSource(string device)
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return null;
        var paths = new PathInfo[np];
        var modes = new byte[nm * 64]; // DISPLAYCONFIG_MODE_INFO
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, 0) != 0) return null;
        for (int i = 0; i < np; i++)
        {
            var name = new SourceName { Header = new Header { Type = 1, Size = Marshal.SizeOf<SourceName>(), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId } };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 && string.Equals(name.GdiName, device, StringComparison.OrdinalIgnoreCase))
                return (paths[i].SourceAdapter, paths[i].SourceId);
        }
        return null;
    }

    // ---------------- interop ----------------

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_DISPLAYORIENTATION = 0x80, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public int Type; public int Size; public Luid Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiGet { public Header Header; public int Min, Cur, Max; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiSet { public Header Header; public int Scale; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName; }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 bytes); only the source part is read.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct PathInfo { public Luid SourceAdapter; public uint SourceId; public uint SourceModeIndex; public uint SourceFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DEVMODE dm, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] byte[] modes, nint topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DpiGet info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DpiSet info);
}
