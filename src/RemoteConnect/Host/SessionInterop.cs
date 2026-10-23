using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>Session / desktop plumbing that lets a SYSTEM host reach the lock screen, sign-in and UAC desktops.</summary>
internal static unsafe partial class SessionInterop
{
    public static bool IsSystem { get; } = WindowsIdentity.GetCurrent().IsSystem;

    private const uint TOKEN_ALL_ACCESS = 0xF01FF, MAXIMUM_ALLOWED = 0x02000000, GENERIC_ALL = 0x10000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1, TokenSessionId = 12;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400, NORMAL_PRIORITY_CLASS = 0x20;
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existing, uint access, nint attributes, int impersonation, int type, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(nint token, int infoClass, void* info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(nint token, string? app, System.Text.StringBuilder cmdLine, nint procAttr, nint threadAttr,
        bool inherit, uint flags, nint env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint env, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint env);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(nint desktop);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(nint obj, int index, void* info, int length, out int needed);

    /// <summary>Generates Ctrl+Alt+Del. Only works from a service with policy SoftwareSASGeneration ≥ 1.</summary>
    [LibraryImport("sas.dll")]
    public static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);

    /// <summary>Starts <paramref name="exe"/> as SYSTEM inside <paramref name="sessionId"/> (caller must be SYSTEM).</summary>
    public static Process LaunchAsSystem(uint sessionId, string exe, string args)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var own)) throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, 0, SecurityImpersonation, TokenPrimary, out var dup)) throw new Win32Exception();
            try
            {
                if (!SetTokenInformation(dup, TokenSessionId, &sessionId, sizeof(uint))) throw new Win32Exception();
                return Create(dup, exe, args, 0);
            }
            finally { CloseHandle(dup); }
        }
        finally { CloseHandle(own); }
    }

    /// <summary>Starts <paramref name="exe"/> as the user logged on to <paramref name="sessionId"/> (caller must be SYSTEM).</summary>
    public static Process? LaunchAsSessionUser(uint sessionId, string exe, string args)
    {
        if (!WTSQueryUserToken(sessionId, out var token)) return null; // nobody signed in
        try
        {
            CreateEnvironmentBlock(out var env, token, false);
            try { return Create(token, exe, args, env); }
            finally { if (env != 0) DestroyEnvironmentBlock(env); }
        }
        finally { CloseHandle(token); }
    }

    private static Process Create(nint token, string exe, string args, nint env)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        var cmd = new System.Text.StringBuilder($"\"{exe}\" {args}");
        if (!CreateProcessAsUserW(token, exe, cmd, 0, 0, false, CREATE_UNICODE_ENVIRONMENT | NORMAL_PRIORITY_CLASS, env,
                Path.GetDirectoryName(exe), ref si, out var pi))
            throw new Win32Exception();
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Process.GetProcessById(pi.dwProcessId);
    }

    public static string? DesktopName(nint desktop)
    {
        char* buf = stackalloc char[128];
        return GetUserObjectInformationW(desktop, UOI_NAME, buf, 256, out _) ? new string(buf) : null;
    }

    /// <summary>
    /// Keeps the calling thread attached to the desktop that currently receives input (Default, Winlogon for
    /// lock / sign-in / UAC, Screen-saver). Capture and SendInput only work on that desktop. One per thread;
    /// the thread must own no windows or hooks. No-op unless running as SYSTEM.
    /// </summary>
    public sealed class InputDesktopFollower : IDisposable
    {
        private nint _current;
        public string? Name { get; private set; }

        /// <summary>Returns true if the thread switched to a different desktop.</summary>
        public bool Sync()
        {
            if (!IsSystem) return false;
            var h = OpenInputDesktop(0, false, GENERIC_ALL);
            if (h == 0) return false;
            var name = DesktopName(h);
            if (name == Name) { CloseDesktop(h); return false; }
            if (!SetThreadDesktop(h))
            {
                Log.Write($"SetThreadDesktop({name}) failed: {Marshal.GetLastPInvokeError()}");
                CloseDesktop(h);
                return false;
            }
            if (_current != 0) CloseDesktop(_current);
            _current = h;
            Log.Write($"Thread {Environment.CurrentManagedThreadId} now on desktop '{name}' (was '{Name}')");
            Name = name;
            return true;
        }

        /// <summary>Has the input desktop changed since the last Sync (without switching)?</summary>
        public bool Changed()
        {
            if (!IsSystem) return false;
            var h = OpenInputDesktop(0, false, GENERIC_ALL);
            if (h == 0) return false;
            var name = DesktopName(h);
            CloseDesktop(h);
            return name != Name;
        }

        public void Dispose()
        {
            if (_current != 0) CloseDesktop(_current);
            _current = 0;
        }
    }
}
