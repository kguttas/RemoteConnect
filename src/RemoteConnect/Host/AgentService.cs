using System.ServiceProcess;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// LocalSystem service (session 0). Session 0 cannot see any screen, so it keeps one host process ("agent")
/// running as SYSTEM inside the active console session, restarting it when the session changes (log-off,
/// user switching) or the agent dies. It also performs Ctrl+Alt+Del, which only a service may generate.
/// </summary>
public sealed class AgentService : ServiceBase
{
    public const string SasEventName = @"Global\RemoteConnect.SAS";

    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private EventWaitHandle? _sas;
    private Process? _agent;
    private uint _agentSession = uint.MaxValue;

    public AgentService()
    {
        ServiceName = Installer.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _sas = new EventWaitHandle(false, EventResetMode.AutoReset, SasEventName);
        _thread = new Thread(Loop) { IsBackground = true, Name = "AgentSupervisor" };
        _thread.Start();
        Log.Write("Service started");
    }

    protected override void OnStop()
    {
        _stop.Set();
        _thread?.Join(5000);
        KillAgent();
        Log.Write("Service stopped");
    }

    protected override void OnShutdown() => OnStop();

    private void Loop()
    {
        int quickFailures = 0;
        DateTime started = DateTime.MinValue;
        var waits = new WaitHandle[] { _stop, _sas! };
        while (true)
        {
            int signaled = WaitHandle.WaitAny(waits, 1000);
            if (signaled == 0) return;
            if (signaled == 1)
            {
                Log.Write("Sending Ctrl+Alt+Del");
                try { SessionInterop.SendSAS(false); } catch (Exception ex) { Log.Error("SendSAS", ex); }
                continue;
            }

            uint session = SessionInterop.WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) continue; // between sessions
            bool alive = _agent is { HasExited: false };
            if (alive && session == _agentSession) continue;

            if (!alive && _agent is not null)
            {
                // Crash loop guard: an agent dying within seconds, repeatedly, waits before the next try.
                quickFailures = DateTime.UtcNow - started < TimeSpan.FromSeconds(10) ? quickFailures + 1 : 0;
                Log.Write($"Agent exited (code {SafeExitCode(_agent)}), restarting");
                if (quickFailures >= 3 && _stop.WaitOne(TimeSpan.FromSeconds(Math.Min(60, 5 * quickFailures)))) return;
            }
            KillAgent();
            try
            {
                _agent = SessionInterop.LaunchAsSystem(session, Environment.ProcessPath!, "--agent");
                _agentSession = session;
                started = DateTime.UtcNow;
                Log.Write($"Agent {_agent.Id} launched in session {session}");
            }
            catch (Exception ex)
            {
                Log.Error($"launch agent in session {session}", ex);
                if (_stop.WaitOne(5000)) return;
            }
        }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "?"; }
    }

    private void KillAgent()
    {
        if (_agent is null) return;
        try
        {
            if (!_agent.HasExited) { _agent.Kill(); _agent.WaitForExit(3000); }
        }
        catch (Exception ex) { Log.Error("kill agent", ex); }
        _agent.Dispose();
        _agent = null;
    }
}
