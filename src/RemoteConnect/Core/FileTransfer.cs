namespace RemoteConnect.Core;

/// <summary>
/// Writes incoming files (FileBegin / FileChunk / FileEnd) under <paramref name="root"/>, never outside it.
/// Used by the host for uploads and by the client for downloads.
/// </summary>
public sealed class FileReceiver(string root, Action<FileAckInfo> ack) : IDisposable
{
    private readonly Dictionary<int, (FileStream Stream, string Path)> _open = new();

    public void Begin(FileHeader h)
    {
        try
        {
            var parts = h.RelativePath.Split('/', '\\')
                .Select(s => string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim())
                .Where(s => s.Length > 0 && s != "." && s != "..")
                .ToArray();
            if (parts.Length == 0) throw new InvalidDataException("Nombre de archivo no válido");
            var full = Path.GetFullPath(Path.Combine([root, .. parts]));
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta fuera de la carpeta");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            full = Unique(full);
            _open[h.Id] = (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20), full);
        }
        catch (Exception ex)
        {
            ack(new FileAckInfo { Id = h.Id, Ok = false, Error = ex.Message });
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public void Chunk(int id, ReadOnlySpan<byte> data)
    {
        if (_open.TryGetValue(id, out var f)) f.Stream.Write(data);
    }

    public void End(int id)
    {
        if (!_open.Remove(id, out var f)) return;
        f.Stream.Dispose();
        ack(new FileAckInfo { Id = id, Ok = true, Path = f.Path });
    }

    /// <summary>Drops every half-received file (cancelled transfer).</summary>
    public void Abort() => Dispose();

    public void Dispose()
    {
        foreach (var (stream, path) in _open.Values)
        {
            stream.Dispose();
            try { File.Delete(path); } catch { }
        }
        _open.Clear();
    }
}
