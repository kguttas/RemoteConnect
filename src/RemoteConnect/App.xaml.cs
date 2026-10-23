using System.Security.Principal;
using System.Windows;
using RemoteConnect.Core;
using RemoteConnect.Host;
using RemoteConnect.UI;

namespace RemoteConnect;

/// <summary>
/// One executable, several roles:
///   (no args)      UI: connect to other PCs and configure this one
///   --service      Windows service (LocalSystem): keeps an --agent running in the active console session
///   --agent        host with tray icon, running as SYSTEM in the user's session (reaches the lock screen)
///   --host         host with tray icon as the current user (portable use, no lock screen)
///   --install user / --uninstall / --firewall / --start-service   elevated setup steps; --set-password pwd
///   --selftest     encoder/decoder/capture diagnostics; --testclient addr pwd secs  headless end-to-end test
/// </summary>
public partial class App : Application
{
    private HostTray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Error("UI", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("unhandled", (Exception)ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        var args = e.Args;
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "--install":
                Shutdown(Installer.Install(args.Length > 1 ? args[1] : WindowsIdentity.GetCurrent().Name));
                return;
            case "--uninstall":
                Shutdown(Installer.Uninstall());
                return;
            case "--set-password" when args.Length > 1:
            {
                var hs = HostSettings.Load();
                hs.SetPassword(args[1]);
                hs.Save();
                Shutdown(0);
                return;
            }
            case "--service":
                System.ServiceProcess.ServiceBase.Run(new AgentService());
                Shutdown(0);
                return;
            case "--start-service":
                Shutdown(Installer.StartService());
                return;
            case "--firewall":
                Shutdown(Installer.OpenFirewallForCurrent());
                return;
            case "--selftest":
            case "--testclient":
                _ = SelfTest.RunAsync(args).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.IsFaulted ? 1 : t.Result)));
                return;
            case "--agent":
            case "--host":
                _tray = HostTray.TryStart(this);
                if (_tray is null) Shutdown(0); // already running
                return;
            default:
                ShutdownMode = ShutdownMode.OnLastWindowClose;
                new MainWindow().Show();
                return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
