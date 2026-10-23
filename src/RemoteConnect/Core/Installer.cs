using System.ComponentModel;

namespace RemoteConnect.Core;

/// <summary>
/// Installs to Program Files, opens the firewall for the exe and registers a LocalSystem service. The service
/// runs the host inside the active console session as SYSTEM, so it also works on the lock / sign-in screen
/// and on UAC prompts, and survives log-off and user switching.
/// </summary>
public static class Installer
{
    public const string ServiceName = "RemoteConnect";
    public const string FirewallRule = "RemoteConnect";
    private const string LegacyTaskName = "RemoteConnect Host"; // v1.0 used a logon task instead of a service
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteConnect");
    public static string InstalledExe => Path.Combine(InstallDir, "RemoteConnect.exe");
    public static string CurrentExe => Environment.ProcessPath!;
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "RemoteConnect.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe) && ServiceExists;

    public static bool ServiceExists => Run("sc.exe", $"query {ServiceName}") == 0;

    public static bool ServiceRunning
    {
        get
        {
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(ServiceName);
                return sc.Status is System.ServiceProcess.ServiceControllerStatus.Running or System.ServiceProcess.ServiceControllerStatus.StartPending;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static bool RunningFromInstallDir =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-launches this exe elevated with the given arguments and waits. False if the user declined UAC.</summary>
    public static bool RunElevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(CurrentExe, args) { UseShellExecute = true, Verb = "runas" });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    /// <param name="user">The interactive user (DOMAIN\name) who gets write access to the host settings.</param>
    public static int Install(string user)
    {
        try
        {
            // Settings must exist before the SYSTEM service reads them: this process runs as the user (elevated),
            // so defaults such as the receive folder point at the user's profile, not SYSTEM's.
            HostSettings.Load();
            SecureHostDir(user);

            Run("schtasks", $"/Delete /TN \"{LegacyTaskName}\" /F");
            StopService();
            StopHosts();
            StopInstalledProcesses();
            if (!RunningFromInstallDir)
            {
                Directory.CreateDirectory(InstallDir);
                if (IsSingleFile) CopyFile(CurrentExe, InstalledExe);
                else CopyDirectory(AppContext.BaseDirectory, InstallDir); // dev build: bin folder
            }
            AddFirewallRule(FirewallRule, InstalledExe);
            CreateService();
            AllowServiceSas();
            CreateShortcut();
            if (Run("sc.exe", $"start {ServiceName}") != 0) throw new InvalidOperationException("No se pudo iniciar el servicio");
            Log.Write($"Installed service for {user} in {InstallDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("install", ex);
            return 1;
        }
    }

    public static int Uninstall()
    {
        try
        {
            StopService();
            Run("sc.exe", $"delete {ServiceName}");
            Run("schtasks", $"/Delete /TN \"{LegacyTaskName}\" /F");
            Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
            StopHosts();
            StopInstalledProcesses();
            try { File.Delete(ShortcutPath); } catch { }
            if (Directory.Exists(InstallDir))
            {
                if (RunningFromInstallDir || !TryDeleteDirectory(InstallDir))
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
                        { CreateNoWindow = true, UseShellExecute = false });
            }
            Log.Write("Uninstalled");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("uninstall", ex);
            return 1;
        }
    }

    /// <summary>Firewall rule for the exe currently running (for portable use without installing).</summary>
    public static int OpenFirewallForCurrent()
    {
        AddFirewallRule(FirewallRule + " (portable)", CurrentExe);
        return 0;
    }

    public static int StartService() => Run("sc.exe", $"start {ServiceName}") == 0 ? 0 : 1;

    /// <summary>Starts the host: through the service when installed (needs elevation), else as a user process.</summary>
    public static void StartHost()
    {
        if (IsInstalled) RunElevated("--start-service");
        else Process.Start(new ProcessStartInfo(CurrentExe, "--host") { UseShellExecute = false });
    }

    private static void StopService()
    {
        if (!ServiceExists) return;
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex) { Log.Error("stop service", ex); }
    }

    /// <summary>Stops a running host process (only the host: the UI that launched us must survive).</summary>
    public static void StopHosts()
    {
        var status = JsonFile.Load<HostStatus>(Paths.HostStatusFile);
        if (status.Pid == 0 || status.Pid == Environment.ProcessId) return;
        try
        {
            using var p = Process.GetProcessById(status.Pid);
            if (!p.ProcessName.Equals("RemoteConnect", StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(Paths.HostCommandFile, "stop");
            if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
        }
        catch (ArgumentException) { } // not running
        catch (Exception ex) { Log.Error("stop host", ex); }
    }

    private static void CreateService()
    {
        var bin = $"\\\"{InstalledExe}\\\" --service";
        int r = ServiceExists
            ? Run("sc.exe", $"config {ServiceName} binPath= \"{bin}\" start= auto obj= LocalSystem")
            : Run("sc.exe", $"create {ServiceName} binPath= \"{bin}\" start= auto obj= LocalSystem DisplayName= \"RemoteConnect\"");
        if (r != 0) throw new InvalidOperationException("sc.exe no pudo registrar el servicio");
        Run("sc.exe", $"description {ServiceName} \"RemoteConnect: acceso remoto a este equipo (incluida la pantalla de inicio de sesión)\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/2000/restart/5000/restart/10000");
    }

    /// <summary>Lets services generate Ctrl+Alt+Del with SendSAS (policy SoftwareSASGeneration ≥ 1).</summary>
    private static void AllowServiceSas()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
        if (k.GetValue("SoftwareSASGeneration") is int v && v is 1 or 3) return;
        k.SetValue("SoftwareSASGeneration", 1, Microsoft.Win32.RegistryValueKind.DWord);
    }

    /// <summary>
    /// The host folder holds the password hash and the TLS private key: only SYSTEM, Administrators and the
    /// installing user may access it (ProgramData would otherwise let every local user read it).
    /// </summary>
    private static void SecureHostDir(string user)
    {
        var dir = Paths.HostDir;
        int r = Run("icacls", $"\"{dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F \"{user}\":(OI)(CI)M");
        if (r != 0) throw new InvalidOperationException("icacls no pudo proteger " + dir);
        Run("icacls", $"\"{dir}\\*\" /reset /T /C /Q");
    }

    private static void AddFirewallRule(string name, string exe)
    {
        Run("netsh", $"advfirewall firewall delete rule name=\"{name}\"");
        int r = Run("netsh", $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
        if (r != 0) throw new InvalidOperationException("netsh no pudo crear la regla del firewall (¿sin permisos de administrador?)");
    }

    private static void CreateShortcut()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.Description = "RemoteConnect - escritorio remoto";
            link.Save();
        }
        catch (Exception ex) { Log.Error("shortcut", ex); }
    }

    /// <summary>The release build is one self-contained exe; only that file is ever copied.</summary>
#pragma warning disable IL3000 // empty Location is exactly the single-file signal we want
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// Ends every process running the installed exe (the SYSTEM agent outlives a stopped service for a moment,
    /// and a window opened from the Start menu runs it too): either one keeps the file locked.
    /// </summary>
    private static void StopInstalledProcesses()
    {
        foreach (var p in Process.GetProcessesByName("RemoteConnect"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) continue;
                    Log.Write($"Installer: ending process {p.Id} ({path})");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) { Log.Write($"Installer: could not end process {p.Id}: {ex.Message}"); }
            }
        }
    }

    private static bool TryDeleteDirectory(string dir)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Directory.Delete(dir, true); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
        return false;
    }

    private static void CopyFile(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 10) { Thread.Sleep(300); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still locked: a running exe can't be overwritten but can be renamed. Move it aside, put the new one
                // in place, and remove the old one once nothing uses it (at the latest on the next reboot).
                var aside = to + $".old-{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(to, aside);
                File.Copy(from, to, false);
                if (!MoveFileExW(aside, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */)) Log.Write($"Installer: {aside} left behind");
                Log.Write($"Installer: {to} was in use; old copy moved to {aside}");
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? replacement, int flags);

    private static void CopyDirectory(string from, string to)
    {
        from = Path.GetFullPath(from);
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    public static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"run {exe}", ex);
            return -1;
        }
    }
}
