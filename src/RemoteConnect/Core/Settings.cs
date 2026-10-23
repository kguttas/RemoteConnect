using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteConnect.Core;

public static class Paths
{
    /// <summary>Per-user data: client settings (saved passwords are DPAPI-protected for this user).</summary>
    public static string UserDir { get; } = CreateDir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteConnect");

    /// <summary>
    /// Host data, shared by the SYSTEM service and the user's UI: ProgramData, locked down by the installer.
    /// </summary>
    public static string HostDir { get; } = Environment.GetEnvironmentVariable(HostDirOverrideVariable) is { Length: > 0 } dir
        ? CreateDir(Path.GetFullPath(dir), "")
        : CreateDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteConnect");

    /// <summary>Testing: a second, isolated host on this machine (own settings, certificate and single-instance mutex).</summary>
    public const string HostDirOverrideVariable = "REMOTECONNECT_HOSTDIR";
    public static bool HostDirOverridden => Environment.GetEnvironmentVariable(HostDirOverrideVariable) is { Length: > 0 };

    public static string HostSettingsFile => Path.Combine(HostDir, "host.json");
    public static string HostStatusFile => Path.Combine(HostDir, "host-status.json");
    public static string HostCommandFile => Path.Combine(HostDir, "host-command.txt");
    public static string CertificateFile => Path.Combine(HostDir, "host.pfx");
    public static string LogFile => Path.Combine(HostDir, "remoteconnect.log");
    public static string ClientSettingsFile => Path.Combine(UserDir, "client.json");

    public static string DefaultReceiveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemoteConnect");

    /// <summary>
    /// v1.0 kept the host identity in %AppData%. Carry the Id, password and certificate over so known
    /// clients keep trusting this machine. Only possible from the user's own process (UI or installer).
    /// </summary>
    public static void MigrateLegacyHostFiles()
    {
        foreach (var name in new[] { "host.json", "host.pfx" })
        {
            var from = Path.Combine(UserDir, name);
            var to = Path.Combine(HostDir, name);
            try
            {
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreateDir(string root, string name)
    {
        var p = Path.Combine(root, name);
        try { Directory.CreateDirectory(p); } catch (UnauthorizedAccessException) { }
        return p;
    }
}

public static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");
}

public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static T Load<T>(string path) where T : new()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new T();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
            }
            catch (IOException) { Thread.Sleep(30); }
            catch (JsonException ex) { Log.Error($"parse {path}", ex); return new T(); }
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class HostSettings
{
    public string HostId { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = Environment.MachineName;
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public string ReceiveFolder { get; set; } = Paths.DefaultReceiveFolder;
    public bool KeepAwake { get; set; } = true;
    public bool AllowAudio { get; set; } = true;
    public bool AllowClipboard { get; set; } = true;
    /// <summary>Let the client browse this machine's files and download them (as the signed-in user).</summary>
    public bool AllowFileDownload { get; set; } = true;
    /// <summary>auto, h264_nvenc, h264_qsv, h264_amf, libx264, h264_mf. Names a vendor: NVENC / QSV / AMF also apply to HEVC and AV1.</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>Accept connections over the Internet (STUN + public signalling + QUIC hole punching).</summary>
    public bool InternetEnabled { get; set; } = true;
    public string SignalServer { get; set; } = P2p.DefaultSignalServer;
    /// <summary>Signalling key derived from the password and the Internet ID (base64); set with the password.</summary>
    public string? P2pKey { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    [System.Text.Json.Serialization.JsonIgnore]
    public string InternetId => P2p.InternetIdFor(HostId);

    public static HostSettings Load()
    {
        Paths.MigrateLegacyHostFiles();
        var s = JsonFile.Load<HostSettings>(Paths.HostSettingsFile);
        if (!File.Exists(Paths.HostSettingsFile)) s.Save(); // persist the generated HostId
        return s;
    }

    public void Save() => JsonFile.Save(Paths.HostSettingsFile, this);

    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(Security.HashPassword(password, salt));
        P2pKey = Convert.ToBase64String(P2p.DeriveKey(password, InternetId));
    }

    public bool CheckPassword(string password)
    {
        if (!HasPassword || PasswordSalt is null) return false;
        var expected = Convert.FromBase64String(PasswordHash!);
        var actual = Security.HashPassword(password, Convert.FromBase64String(PasswordSalt));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class HostStatus
{
    public int Pid { get; set; }
    public bool Listening { get; set; }
    public bool Elevated { get; set; }
    public int Port { get; set; }
    public string? Error { get; set; }
    public string? SessionClient { get; set; }
    public string? SessionAddress { get; set; }
    public DateTime? SessionSince { get; set; }
    public string? Encoder { get; set; }
    /// <summary>Running as SYSTEM under the service: lock screen, sign-in and UAC are reachable.</summary>
    public bool SystemService { get; set; }
    /// <summary>Internet (P2P) availability text, null when disabled.</summary>
    public string? Internet { get; set; }
    public DateTime Updated { get; set; }
}

public sealed class KnownHost
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public string LastAddress { get; set; } = "";
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    public string? Fingerprint { get; set; }
    public string? Mac { get; set; }
    /// <summary>9-digit Internet ID: lets the client reach the host from outside the LAN.</summary>
    public string? InternetId { get; set; }
    public string? ProtectedPassword { get; set; }
    public DateTime? LastConnected { get; set; }

    public string? GetPassword() => ProtectedPassword is null ? null : Security.Unprotect(ProtectedPassword);
    public void SetPassword(string? password) => ProtectedPassword = password is null ? null : Security.Protect(password);
}

public sealed class ClientSettings
{
    public List<KnownHost> Hosts { get; set; } = new();
    public StreamOptions Defaults { get; set; } = new();
    public bool FitToWindow { get; set; } = true;
    public bool CaptureSystemKeys { get; set; } = true;
    /// <summary>Where files downloaded from remote machines go.</summary>
    public string DownloadFolder { get; set; } = Paths.DefaultReceiveFolder;
    public string SignalServer { get; set; } = P2p.DefaultSignalServer;

    public static ClientSettings Load() => JsonFile.Load<ClientSettings>(Paths.ClientSettingsFile);
    public void Save() => JsonFile.Save(Paths.ClientSettingsFile, this);
}

public static class Security
{
    public static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);

    public static string Protect(string text) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string data)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public static string Fingerprint(System.Security.Cryptography.X509Certificates.X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>Short human-comparable form of a fingerprint, e.g. "3FA2-91C0-77DE".</summary>
    public static string ShortFingerprint(string? fp) =>
        string.IsNullOrEmpty(fp) || fp.Length < 12 ? "?" : $"{fp[..4]}-{fp[4..8]}-{fp[8..12]}";

    public static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadOrCreateHostCertificate()
    {
        using System.Security.Cryptography.X509Certificates.X509Certificate2 cert = LoadOrCreatePfx();
        // Reload with a persisted key: Schannel cannot use ephemeral keys for a TLS server.
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadOrCreatePfx()
    {
        var path = Paths.CertificateFile;
        Paths.MigrateLegacyHostFiles();
        if (File.Exists(path))
        {
            try
            {
                return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(path, null,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable |
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (Exception ex) { Log.Error("load certificate, regenerating", ex); }
        }
        using var rsa = RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN=RemoteConnect {Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        File.WriteAllBytes(path, created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        return created;
    }
}
