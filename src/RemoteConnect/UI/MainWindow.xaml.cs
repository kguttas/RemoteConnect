using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteConnect.Client;
using RemoteConnect.Core;

namespace RemoteConnect.UI;

public sealed class HostItem
{
    public KnownHost? Known { get; init; }
    public DiscoveredHost? Found { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public Brush StatusBrush { get; init; } = Brushes.Gray;
    public Visibility WakeVisibility { get; init; } = Visibility.Collapsed;
    public Visibility ForgetVisibility { get; init; } = Visibility.Collapsed;
}

public partial class MainWindow : Window
{
    private static readonly Brush Online = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Busy = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Offline = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));

    private readonly DiscoveryBrowser _discovery = new();
    private readonly ClientSettings _client = ClientSettings.Load();
    private readonly DispatcherTimer _timer;
    private HostSettings _host = HostSettings.Load();
    private bool _loading;
    private bool _hostRunning;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public MainWindow()
    {
        InitializeComponent();
        _discovery.Changed += () => Dispatcher.BeginInvoke(RefreshHosts);
        _discovery.Start();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => { RefreshHosts(); RefreshHostStatus(); };
        _timer.Start();
        LogPathRun.Text = Paths.LogFile;
        LoadHostSettings();
        RefreshHosts();
        RefreshHostStatus();
        Closed += (_, _) => _discovery.Dispose();
        if (!_host.HasPassword && !_client.Hosts.Any())
            Loaded += (_, _) => { }; // first run: both tabs are self-explanatory
    }

    // ================= Connect tab =================

    private void RefreshHosts()
    {
        var items = new List<HostItem>();
        var found = _discovery.Hosts.Where(h => DateTime.UtcNow - h.LastSeen < TimeSpan.FromSeconds(12)).ToList();
        foreach (var k in _client.Hosts.OrderByDescending(h => h.LastConnected))
        {
            var f = found.FirstOrDefault(x => x.Beacon.Id == k.Id);
            items.Add(MakeItem(k, f));
        }
        foreach (var f in found.Where(f => _client.Hosts.All(k => k.Id != f.Beacon.Id)).OrderBy(f => f.Beacon.Name))
            items.Add(MakeItem(null, f));
        HostList.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HostItem MakeItem(KnownHost? k, DiscoveredHost? f)
    {
        string name = f?.Beacon.Name ?? k!.Name;
        string machine = f?.Beacon.Machine ?? k!.Machine;
        string status;
        Brush brush;
        if (f is null)
        {
            status = (string.IsNullOrEmpty(k!.InternetId)
                         ? $"Sin conexión · última IP {k.LastAddress}"
                         : $"No está en esta red · por Internet con ID {P2p.FormatId(k.InternetId)}")
                     + (k.LastConnected is { } lc ? $" · usado {lc:g}" : "");
            brush = Offline;
        }
        else
        {
            status = $"{(f.Beacon.Busy ? "En línea · sesión activa" : "En línea")} · {f.Address}" +
                     (k is null ? " · nuevo" : "");
            brush = f.Beacon.Busy ? Busy : Online;
        }
        return new HostItem
        {
            Known = k,
            Found = f,
            Title = string.Equals(name, machine, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(machine) ? name : $"{name}  ({machine})",
            Subtitle = status,
            StatusBrush = brush,
            WakeVisibility = f is null && !string.IsNullOrEmpty(k?.Mac) ? Visibility.Visible : Visibility.Collapsed,
            ForgetVisibility = k is not null ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    private void OnConnectHost(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HostItem item) return;
        var known = item.Known ?? new KnownHost
        {
            Id = item.Found!.Beacon.Id,
            Name = item.Found.Beacon.Name,
            Machine = item.Found.Beacon.Machine,
            LastAddress = item.Found.Address.ToString(),
            Port = item.Found.Beacon.Port,
            Mac = item.Found.Beacon.Mac,
        };
        Connect(known);
    }

    private void OnConnectAddress(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim();
        if (text.Length == 0) { AddressBox.Focus(); return; }
        if (P2p.TryParseId(text, out var internetId))
        {
            Connect(_client.Hosts.FirstOrDefault(h => h.InternetId == internetId)
                    ?? new KnownHost { Name = $"ID {P2p.FormatId(internetId)}", InternetId = internetId });
            return;
        }
        int port = ProtocolInfo.DefaultPort;
        var hostPart = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p)) { port = p; hostPart = text[..colon]; }
        var existing = _client.Hosts.FirstOrDefault(h => string.Equals(h.LastAddress, hostPart, StringComparison.OrdinalIgnoreCase) && h.Port == port);
        Connect(existing ?? new KnownHost { Name = hostPart, LastAddress = hostPart, Port = port });
    }

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConnectAddress(sender, e);
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) =>
        AddressHint.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Connect(KnownHost host)
    {
        var password = host.GetPassword();
        if (password is null)
        {
            var dlg = new PasswordDialog(host.Name) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            password = dlg.Password;
            if (dlg.Remember) host.SetPassword(password);
        }

        var opts = _client.Defaults.Clone();
        opts.MonitorIndex = 0;
        var session = new RemoteSession(host, password, opts, _discovery, _client.SignalServer);
        session.HostUpdated += h => Dispatcher.BeginInvoke(() =>
        {
            // Same machine may have been added by address before: merge by Id.
            _client.Hosts.RemoveAll(x => !ReferenceEquals(x, h) && (x.Id == h.Id || (string.IsNullOrEmpty(x.Id)
                && ((x.InternetId is not null && x.InternetId == h.InternetId) || (x.InternetId is null && x.LastAddress == h.LastAddress)))));
            if (!_client.Hosts.Contains(h)) _client.Hosts.Add(h);
            _client.Save();
            RefreshHosts();
        });
        new RemoteWindow(session, _client).Show();
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HostItem { Known: { } k }) return;
        if (MessageBox.Show(this, $"¿Olvidar «{k.Name}»? Se borrará la contraseña guardada y la huella de seguridad.", "RemoteConnect",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _client.Hosts.Remove(k);
        _client.Save();
        RefreshHosts();
    }

    private void OnWake(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HostItem { Known.Mac: { } mac }) return;
        NetUtil.SendWakeOnLan(mac);
        MessageBox.Show(this, "Paquete Wake-on-LAN enviado. El equipo debería aparecer en línea en unos segundos.", "RemoteConnect");
    }

    // ================= Host tab =================

    private void LoadHostSettings()
    {
        _loading = true;
        _host = HostSettings.Load();
        EnabledToggle.IsChecked = _host.Enabled;
        EnabledToggle.Content = _host.Enabled ? "Conexiones permitidas" : "Conexiones bloqueadas";
        HostNameBox.Text = _host.Name;
        PortBox.Text = _host.Port.ToString();
        FolderBox.Text = _host.ReceiveFolder;
        KeepAwakeBox.IsChecked = _host.KeepAwake;
        AudioBox.IsChecked = _host.AllowAudio;
        ClipboardBox.IsChecked = _host.AllowClipboard;
        FileDownloadBox.IsChecked = _host.AllowFileDownload;
        EncoderBox.SelectedItem = EncoderBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _host.Encoder) ?? EncoderBox.Items[0];
        InternetToggle.IsChecked = _host.InternetEnabled;
        InternetToggle.Content = _host.InternetEnabled ? "Permitido por Internet" : "Solo red local";
        InternetIdText.Text = P2p.FormatId(_host.InternetId);
        try
        {
            if (File.Exists(Paths.CertificateFile))
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(Paths.CertificateFile, null, X509KeyStorageFlags.EphemeralKeySet);
                FingerprintText.Text = $"Huella de este equipo: {Security.ShortFingerprint(Security.Fingerprint(cert))}";
            }
            else FingerprintText.Text = "La huella se genera al iniciar el host por primera vez.";
        }
        catch { FingerprintText.Text = ""; }
        _loading = false;
    }

    private void SaveHost()
    {
        _host.Save();
        RefreshHostStatus();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        _host.Enabled = EnabledToggle.IsChecked == true;
        EnabledToggle.Content = _host.Enabled ? "Conexiones permitidas" : "Conexiones bloqueadas";
        SaveHost();
        if (_host.Enabled && !_hostRunning) Installer.StartHost();
    }

    private void OnInternetChanged(object sender, RoutedEventArgs e)
    {
        _host.InternetEnabled = InternetToggle.IsChecked == true;
        InternetToggle.Content = _host.InternetEnabled ? "Permitido por Internet" : "Solo red local";
        SaveHost();
    }

    private void OnCopyInternetId(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(P2p.FormatId(_host.InternetId)); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void OnHostOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _host.Name = string.IsNullOrWhiteSpace(HostNameBox.Text) ? Environment.MachineName : HostNameBox.Text.Trim();
        if (int.TryParse(PortBox.Text, out var port) && port is > 0 and < 65536) _host.Port = port;
        else PortBox.Text = _host.Port.ToString();
        _host.KeepAwake = KeepAwakeBox.IsChecked == true;
        _host.AllowAudio = AudioBox.IsChecked == true;
        _host.AllowClipboard = ClipboardBox.IsChecked == true;
        _host.AllowFileDownload = FileDownloadBox.IsChecked == true;
        if (EncoderBox.SelectedItem is ComboBoxItem { Tag: string enc }) _host.Encoder = enc;
        SaveHost();
    }

    private void OnSavePassword(object sender, RoutedEventArgs e)
    {
        if (NewPassword.Password.Length < 6) { MessageBox.Show(this, "La contraseña debe tener al menos 6 caracteres.", "RemoteConnect"); return; }
        if (NewPassword.Password != NewPassword2.Password) { MessageBox.Show(this, "Las contraseñas no coinciden.", "RemoteConnect"); return; }
        _host.SetPassword(NewPassword.Password);
        SaveHost();
        NewPassword.Clear();
        NewPassword2.Clear();
        MessageBox.Show(this, "Contraseña guardada.", "RemoteConnect");
        if (!_hostRunning && _host.Enabled) Installer.StartHost();
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta para archivos recibidos", InitialDirectory = _host.ReceiveFolder };
        if (dlg.ShowDialog(this) != true) return;
        _host.ReceiveFolder = dlg.FolderName;
        FolderBox.Text = dlg.FolderName;
        SaveHost();
    }

    private void RefreshHostStatus()
    {
        var st = JsonFile.Load<HostStatus>(Paths.HostStatusFile);
        _hostRunning = st.Pid != 0 && DateTime.Now - st.Updated < TimeSpan.FromSeconds(10) && ProcessAlive(st.Pid);
        string text;
        if (!_hostRunning) text = "El host no se está ejecutando: este equipo no acepta conexiones.";
        else if (!st.Listening) text = st.Error ?? "Host en ejecución, conexiones bloqueadas.";
        else
        {
            text = $"Activo · puerto {st.Port}" + (st.SystemService
                ? " · servicio del sistema (también pantalla de bloqueo, inicio de sesión y UAC)"
                : st.Elevated ? " · administrador (sin pantalla de bloqueo: instálalo como servicio)"
                : " · sin privilegios (instálalo para controlar la pantalla de bloqueo y ventanas elevadas)");
            if (st.Error is not null) text += $"\n⚠ {st.Error}";
            if (st.SessionClient is not null) text += $"\nSesión activa: {st.SessionClient} desde {st.SessionAddress}{(st.Encoder is null ? "" : $" · {st.Encoder}")}";
        }
        HostStatusText.Text = text;
        InternetIdText.Opacity = _host.InternetEnabled ? 1 : 0.4;
        InternetStatusText.Text = !_host.InternetEnabled ? "Desactivado: solo se aceptan conexiones de la red local."
            : !_hostRunning ? "El host no se está ejecutando."
            : st.Internet ?? "Iniciando…";
        HostAddressText.Text = $"{_host.Name} · IP actual: {NetUtil.LocalAddressesText()}";
        bool installed = Installer.IsInstalled;
        bool service = installed && (st.SystemService || !_hostRunning);
        HostRunButton.Content = service ? (_hostRunning ? "Reiniciar host" : "Iniciar servicio") : _hostRunning ? "Detener host" : "Iniciar host";
        KickButton.Visibility = _hostRunning && st.SessionClient is not null ? Visibility.Visible : Visibility.Collapsed;

        InstallText.Text = installed
            ? $"Instalado en {Installer.InstallDir} como servicio de Windows: arranca con el equipo (antes de iniciar sesión), funciona en la pantalla de bloqueo y en los avisos UAC, y el firewall está abierto."
            : "Instala para que el host se ejecute como servicio de Windows: arranca con el equipo, permite usar la pantalla de bloqueo / inicio de sesión, Ctrl+Alt+Supr y los avisos UAC, y abre el firewall. Necesario en el equipo remoto; opcional en el cliente.";
        InstallButton.Content = installed ? "Reinstalar / actualizar" : "Instalar en este equipo";
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { return !p.HasExited; }
            catch (System.ComponentModel.Win32Exception) { return true; } // SYSTEM agent: exists, but we may not open it
        }
        catch { return false; }
    }

    private void OnHostRun(object sender, RoutedEventArgs e)
    {
        // Under the service, "stop" just makes the service relaunch the agent: a restart.
        if (_hostRunning) File.WriteAllText(Paths.HostCommandFile, "stop");
        else
        {
            if (!_host.HasPassword) MessageBox.Show(this, "Configura primero una contraseña para este equipo.", "RemoteConnect");
            Installer.StartHost();
        }
        Dispatcher.BeginInvoke(RefreshHostStatus, DispatcherPriority.Background);
    }

    private void OnKick(object sender, RoutedEventArgs e) => File.WriteAllText(Paths.HostCommandFile, "disconnect");

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        if (Installer.RunElevated($"--install \"{user}\""))
        {
            MessageBox.Show(this, "RemoteConnect se ha instalado. El host ya está en marcha como servicio, arrancará con el equipo y también funciona en la pantalla de bloqueo.\n" +
                                  (_host.HasPassword ? "" : "\nNo olvides configurar una contraseña."), "RemoteConnect", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else MessageBox.Show(this, "La instalación no se completó (cancelada o sin permisos). Revisa el registro para más detalles.", "RemoteConnect",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshHostStatus();
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "¿Desinstalar RemoteConnect de este equipo? Se detendrá el servicio y se quitará la regla del firewall.",
                "RemoteConnect", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Installer.RunElevated("--uninstall");
        RefreshHostStatus();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Paths.LogFile)) Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
    }
}

/// <summary>Minimal password prompt, built in code to keep the project small.</summary>
public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _box = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _remember = new() { Content = "Recordar en este equipo", Margin = new Thickness(0, 10, 0, 0), IsChecked = true };

    public string Password => _box.Password;
    public bool Remember => _remember.IsChecked == true;

    public PasswordDialog(string hostName)
    {
        Title = "RemoteConnect";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "Conectar", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 100 };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20), Width = 320 };
        panel.Children.Add(new TextBlock { Text = $"Contraseña de «{hostName}»", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_box);
        panel.Children.Add(_remember);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
