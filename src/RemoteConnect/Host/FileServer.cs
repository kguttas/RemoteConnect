using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// Serves this machine's files to the connected client: folder listings and downloads (remote → local), over
/// the Bulk lane so they never get in the way of video, audio or input.
///
/// Installed as a service the host runs as SYSTEM, which could read every user's files. All file access is
/// therefore done impersonating the user signed in to the console session, with exactly that user's rights;
/// with nobody signed in (lock screen after boot) nothing is served.
/// </summary>
public sealed class FileServer(Connection conn) : IDisposable
{
    private const int ChunkBytes = 256 * 1024;
    private CancellationTokenSource? _download;
    private int _nextId = 1 << 30; // far from the client's upload ids

    // ---------------- listing ----------------

    public void List(string path) => Task.Run(() =>
    {
        DirListingInfo info;
        try { info = AsUser(token => path.Length == 0 ? TopLevel(token) : Listing(path)); }
        catch (Exception ex) { info = new DirListingInfo { Path = path, Parent = ParentOf(path), Error = ex.Message }; }
        conn.SendJson(Msg.DirListing, info);
    });

    private static DirListingInfo TopLevel(SafeAccessTokenHandle? token)
    {
        var info = new DirListingInfo { Path = "", Parent = null };
        foreach (var (name, id) in KnownFolders)
            if (KnownFolder(id, token) is { } p && Directory.Exists(p))
                info.Entries.Add(new RemoteEntry { Name = name, Path = p, IsDir = true });
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            string label = d.Name.TrimEnd('\\');
            try { if (!string.IsNullOrEmpty(d.VolumeLabel)) label = $"{d.VolumeLabel} ({label})"; } catch { }
            info.Entries.Add(new RemoteEntry { Name = label, Path = d.RootDirectory.FullName, IsDir = true, Size = d.TotalSize - d.AvailableFreeSpace });
        }
        return info;
    }

    private static DirListingInfo Listing(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        var info = new DirListingInfo { Path = dir.FullName, Parent = ParentOf(dir.FullName) };
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        foreach (var e in dir.EnumerateFileSystemInfos("*", options))
        {
            bool isDir = e is DirectoryInfo;
            info.Entries.Add(new RemoteEntry
            {
                Name = e.Name, Path = e.FullName, IsDir = isDir,
                Size = isDir ? 0 : ((FileInfo)e).Length, Modified = e.LastWriteTime,
            });
        }
        info.Entries.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return info;
    }

    /// <summary>One level up; "" (the top level) from a drive root; null at the top level itself.</summary>
    private static string? ParentOf(string path) => path.Length == 0 ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? "";

    // ---------------- download ----------------

    public void Get(FileGetRequest request)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _download = cts;
        _ = Task.Run(() => SendAsync(request.Paths, cts.Token));
    }

    public void Cancel()
    {
        var old = Interlocked.Exchange(ref _download, null);
        old?.Cancel();
    }

    private async Task SendAsync(List<string> paths, CancellationToken ct)
    {
        List<(string Full, string Relative, long Size)> files;
        try { files = AsUser(_ => Expand(paths)); }
        catch (Exception ex) { Report(DownloadInfo.Phase.Failed, error: ex.Message); return; }
        Report(DownloadInfo.Phase.Started, files.Count, files.Sum(f => f.Size));

        var buffer = new byte[ChunkBytes];
        int sent = 0;
        try
        {
            foreach (var (full, rel, _) in files)
            {
                ct.ThrowIfCancellationRequested();
                FileStream fs;
                try { fs = AsUser(_ => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, true)); }
                catch (Exception ex)
                {
                    conn.SendText(Msg.Notice, $"No se pudo leer {rel}: {ex.Message}");
                    continue;
                }
                await using (fs)
                {
                    int id = Interlocked.Increment(ref _nextId);
                    conn.SendJson(Msg.FileBegin, new FileHeader { Id = id, RelativePath = rel, Size = fs.Length }, Lane.Bulk);
                    int n;
                    while ((n = await fs.ReadAsync(buffer, ct)) > 0)
                    {
                        // Same flow control as uploads: ~2 MB queued keeps the link full and cancelling quick.
                        while (conn.BulkPending > 8)
                        {
                            if (conn.IsClosed) return;
                            await Task.Delay(2, ct);
                        }
                        SendChunk(id, buffer.AsSpan(0, n));
                    }
                    conn.Send(Msg.FileEnd, new PacketWriter().I32(id), Lane.Bulk);
                    sent++;
                }
            }
            Report(DownloadInfo.Phase.Finished, sent);
        }
        catch (OperationCanceledException)
        {
            Report(DownloadInfo.Phase.Cancelled, sent);
        }
        catch (Exception ex)
        {
            Log.Error("file download", ex);
            Report(DownloadInfo.Phase.Failed, sent, error: ex.Message);
        }
    }

    private void SendChunk(int id, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, id);
        conn.Send(Msg.FileChunk, head, data, Lane.Bulk);
    }

    /// <summary>Files to send, with paths relative to what was picked (a picked folder keeps its name).</summary>
    private static List<(string, string, long)> Expand(List<string> paths)
    {
        var list = new List<(string, string, long)>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            if (File.Exists(path))
                list.Add((path, Path.GetFileName(path), new FileInfo(path).Length));
            else if (Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", options))
                    list.Add((f.FullName, Path.GetRelativePath(parent, f.FullName).Replace('\\', '/'), f.Length));
            }
        }
        return list;
    }

    private void Report(DownloadInfo.Phase phase, int files = 0, long bytes = 0, string? error = null) =>
        conn.SendJson(Msg.DownloadInfo, new DownloadInfo { State = phase, Files = files, Bytes = bytes, Error = error }, Lane.Bulk);

    // ---------------- user context ----------------

    /// <summary>Runs <paramref name="f"/> with the signed-in user's rights (and passes their token; null = this process's user).</summary>
    private static T AsUser<T>(Func<SafeAccessTokenHandle?, T> f)
    {
        if (!SessionInterop.IsSystem) return f(null);
        using var token = SessionInterop.SessionUserToken()
            ?? throw new UnauthorizedAccessException("Nadie ha iniciado sesión en el equipo remoto: no se pueden leer archivos.");
        return WindowsIdentity.RunImpersonated(token, () => f(token));
    }

    private static readonly (string Name, Guid Id)[] KnownFolders =
    [
        ("Escritorio", new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        ("Documentos", new("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        ("Descargas", new("374DE290-123F-4565-9164-39C4925E467B")),
        ("Imágenes", new("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        ("Carpeta personal", new("5E6C858F-0E22-4760-9AFE-EA3317B67173")),
    ];

    private static string? KnownFolder(Guid id, SafeAccessTokenHandle? token)
    {
        if (SHGetKnownFolderPath(id, 0, token?.DangerousGetHandle() ?? 0, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);

    public void Dispose() => Cancel();
}
