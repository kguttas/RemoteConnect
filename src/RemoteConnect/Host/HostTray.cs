using System.Windows;
using System.Windows.Threading;
using RemoteConnect.Core;
using Forms = System.Windows.Forms;

namespace RemoteConnect.Host;

/// <summary>Host process shell: single instance, tray icon, connection notifications.</summary>
public sealed class HostTray : IDisposable
{
    private readonly Mutex _mutex;
    private readonly HostService _service;
    private readonly Forms.NotifyIcon _icon;

    private HostTray(Application app, Mutex mutex)
    {
        _mutex = mutex;
        _service = new HostService(Dispatcher.CurrentDispatcher);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir RemoteConnect", null, (_, _) => OpenUi());
        menu.Items.Add("Desconectar sesión", null, (_, _) => _service.DisconnectSession());
        if (!SessionInterop.IsSystem) // under the service the agent is always restarted; stop the service instead
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Detener host", null, (_, _) => app.Shutdown());
        }
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "RemoteConnect: host activo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenUi();
        _service.Notify += text => app.Dispatcher.BeginInvoke(() =>
        {
            _icon.Text = _service.Session is { } s ? $"RemoteConnect: {s.ClientName} conectado" : "RemoteConnect: host activo";
            _icon.ShowBalloonTip(4000, "RemoteConnect", text, Forms.ToolTipIcon.Info);
        });
        _service.ExitRequested += () => app.Dispatcher.BeginInvoke(app.Shutdown);
        _service.Start();
    }

    public static HostTray? TryStart(Application app)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(true, @"Local\RemoteConnect.Host" + (Paths.HostDirOverridden ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Paths.HostDir.ToLowerInvariant())))[..12] : ""), out bool created);
            if (!created) { mutex.Dispose(); return null; }
        }
        catch (UnauthorizedAccessException) { return null; } // owned by an elevated instance
        try { return new HostTray(app, mutex); }
        catch (Exception ex)
        {
            Log.Error("host start", ex);
            mutex.Dispose();
            return null;
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        return res is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(res.Stream, 16, 16);
    }

    private static void OpenUi()
    {
        // The UI must run as the signed-in user, never as SYSTEM or elevated (DPAPI, drag & drop, profile paths).
        if (SessionInterop.IsSystem)
        {
            try { SessionInterop.LaunchAsSessionUser((uint)Process.GetCurrentProcess().SessionId, Environment.ProcessPath!, "")?.Dispose(); }
            catch (Exception ex) { Log.Error("open UI", ex); }
        }
        else if (HostService.IsElevated) Process.Start("explorer.exe", $"\"{Environment.ProcessPath}\"");
        else Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _service.Dispose();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
