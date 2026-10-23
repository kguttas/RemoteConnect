using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteConnect.Client;
using RemoteConnect.Core;

namespace RemoteConnect.UI;

public partial class RemoteWindow : Window
{
    private static readonly int[] Bitrates = [5, 10, 20, 30, 50, 80, 120, 200];
    private static readonly int[] FpsValues = [30, 60, 90, 120, 144];

    private readonly RemoteSession _session;
    private readonly ClientSettings _settings;
    private readonly VideoPipeline _video;
    private readonly AudioPlayer _audio = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly HashSet<(ushort, ushort, KeyFlags)> _keysDown = new();
    private KeyboardHook? _hook;
    private ClipboardBridge? _clipboard;
    private nint _hwnd;
    private bool _loadingOptions;
    private bool _fullScreen;
    private WindowState _prevState;
    private bool _sizedToRemote;
    private long _lastBytes;
    private int _lastShown;
    private VideoFormatInfo? _format;
    private ViewLayout? _layout;
    private double _panX = 0.5, _panY = 0.5;
    private int _buttonsDown;
    private bool _closing;
    private bool _toastWanted;

    public RemoteWindow(RemoteSession session, ClientSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        Title = $"{session.Host.Name} — RemoteConnect";
        _video = new VideoPipeline(() => _session.RequestKeyframe(), windowed: true);

        _loadingOptions = true;
        var o = session.Options;
        foreach (var b in Bitrates) BitrateBox.Items.Add(new ComboBoxItem { Content = $"{b} Mbps", Tag = b });
        foreach (var f in FpsValues) FpsBox.Items.Add(new ComboBoxItem { Content = $"{f} fps", Tag = f });
        BitrateBox.SelectedIndex = Math.Max(0, Array.IndexOf(Bitrates, Bitrates.MinBy(b => Math.Abs(b - o.BitrateMbps))));
        FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(FpsValues, FpsValues.MinBy(f => Math.Abs(f - o.MaxFps))));
        CodecBox.SelectedItem = CodecBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == o.Codec) ?? CodecBox.Items[0];
        AdaptiveToggle.IsChecked = o.AdaptiveBitrate;
        Chroma444.IsChecked = o.Chroma444;
        AudioToggle.IsChecked = o.Audio;
        FitToggle.IsChecked = settings.FitToWindow;
        KeysToggle.IsChecked = settings.CaptureSystemKeys;
        MonitorBox.Items.Add(new ComboBoxItem { Content = $"Monitor {o.MonitorIndex + 1}", Tag = o.MonitorIndex });
        MonitorBox.SelectedIndex = 0;
        _loadingOptions = false;
        ApplyFit();

        _session.StateChanged += (s, text) => Dispatcher.BeginInvoke(() => OnState(s, text));
        _session.Welcome += info => Dispatcher.BeginInvoke(() => OnWelcome(info));
        // Reset on the network thread, before the keyframe that follows VideoFormat reaches the pipeline;
        // doing it via the dispatcher could land after that keyframe and freeze the picture until the next one.
        _session.VideoFormat += f => { _video.Reset(f); Dispatcher.BeginInvoke(() => OnVideoFormat(f)); };
        _session.VideoFrame += p => _video.Push(p);
        _session.Cursor += (kind, hx, hy, w, h, data) => Dispatcher.BeginInvoke(() => Video.CursorHandle = GetCursor(kind, hx, hy, w, h, data));
        _session.Audio += (rate, ch, codec, data) => { if (AudioEnabled) _audio.Add(rate, ch, codec, data); };
        _session.Clipboard += (kind, data) => Dispatcher.BeginInvoke(() => _clipboard?.ApplyRemote(kind, data));
        _session.Notice += text => Dispatcher.BeginInvoke(() => ShowToast(text, 6));
        _session.FileAck += ack => Dispatcher.BeginInvoke(() =>
        {
            if (!ack.Ok) ShowToast($"Error al enviar archivo: {ack.Error}", 8);
            else _lastSavedPath = ack.Path;
        });

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        Video.PixelSizeChanged += (_, _) => UpdateView();
        Video.PaintRequested += _video.RequestRedraw;
        Video.MouseMoved += OnVideoMouseMove;
        Video.MouseButton += OnVideoButton;
        Video.Wheel += (delta, horizontal) => { if (_session.IsConnected) _session.MouseWheel(delta, horizontal); };
        Video.CaptureLost += ReleaseButtons;
        Video.FilesDropped += paths => _ = Upload(paths);
        PreviewKeyDown += OnWpfKey;
        PreviewKeyUp += OnWpfKey;
        PreviewMouseWheel += OnWpfWheel;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Deactivated += (_, _) => { ReleaseAllInput(); ToastPopup.IsOpen = false; };
        Activated += (_, _) => { if (_toastWanted) { ToastPopup.IsOpen = true; PlaceToast(); } };
        MouseMove += OnWindowMouseMove;
        SizeChanged += (_, _) => PlaceToast();
        LocationChanged += (_, _) => PlaceToast();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>For the self-test.</summary>
    internal (int Shown, string Decoder, string Stats) Diagnostics() => (_video.FramesShown, _video.DecoderInfo, Stats.Text);

    private volatile bool _audioEnabled = true;
    private bool AudioEnabled => _audioEnabled;
    private string? _lastSavedPath;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _video.SetWindow(Video.Hwnd);
        UpdateView();
        _hook = new KeyboardHook(() => _hwnd)
        {
            Enabled = KeysToggle.IsChecked == true,
            KeyEvent = OnHookKey,
            ToggleFullScreen = () => Dispatcher.BeginInvoke(ToggleFullScreen),
            ReleaseGrab = () => Dispatcher.BeginInvoke(() => { KeysToggle.IsChecked = false; OnKeysChanged(this, new RoutedEventArgs()); ShowToast("Teclado liberado. Vuelve a activar «Teclas Win» para capturarlo.", 4); }),
        };
        try
        {
            _clipboard = new ClipboardBridge();
            _clipboard.LocalChanged += (kind, data) => { if (_session.IsConnected) _session.SendClipboard(kind, data); };
        }
        catch (Exception ex) { Log.Error("client clipboard", ex); }
        _audioEnabled = AudioToggle.IsChecked == true;
        ScreenArea.Focus();
        _session.Start();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _statsTimer.Stop();
        ToastPopup.IsOpen = false;
        ReleaseAllInput();
        _hook?.Dispose();
        _clipboard?.Dispose();
        await _session.DisposeAsync();
        _video.Dispose();
        _audio.Dispose();
        foreach (var c in _cursors.Values) Native.DestroyIcon(c);
        _cursors.Clear();
    }

    // ---------------- session events ----------------

    private void OnState(SessionState s, string text)
    {
        if (_closing) return;
        switch (s)
        {
            case SessionState.Connected:
                Overlay.Visibility = Visibility.Collapsed;
                Video.Visibility = Visibility.Visible;
                Title = $"{_session.Host.Name} — RemoteConnect";
                break;
            case SessionState.AuthFailed:
                ShowOverlay(text + "\nIntroduce la contraseña del equipo remoto:", spinner: false, password: true);
                break;
            case SessionState.SecurityError:
                ShowOverlay(text, spinner: false, password: false);
                break;
            case SessionState.Closed:
                ShowOverlay(text, spinner: false, password: false);
                break;
            default:
                ShowOverlay(text, spinner: true, password: false);
                break;
        }
    }

    private void ShowOverlay(string text, bool spinner, bool password)
    {
        // WPF can't draw over the native video window: hide it while the overlay is up.
        Video.Visibility = Visibility.Hidden;
        Overlay.Visibility = Visibility.Visible;
        OverlayText.Text = text;
        Spinner.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        if (password) { RetryPassword.Clear(); RetryPassword.Focus(); }
    }

    private void OnWelcome(WelcomeInfo info)
    {
        _loadingOptions = true;
        MonitorBox.Items.Clear();
        int current = _session.Options.MonitorIndex;
        foreach (var m in info.Monitors)
            MonitorBox.Items.Add(new ComboBoxItem { Content = $"Monitor {m.Index + 1} · {m.Width}×{m.Height}{(m.Primary ? " ★" : "")}", Tag = m.Index });
        MonitorBox.SelectedIndex = Math.Clamp(current, 0, Math.Max(0, MonitorBox.Items.Count - 1));
        _loadingOptions = false;
        if (!info.Elevated)
            ShowToast("Aviso: el host no se ejecuta como administrador; no podrás controlar ventanas elevadas (UAC). Instálalo desde «Este equipo».", 8);
    }

    private void OnVideoFormat(VideoFormatInfo f)
    {
        _format = f;
        UpdateView();
        if (!_sizedToRemote && !_fullScreen && WindowState == WindowState.Normal)
        {
            _sizedToRemote = true;
            // Open the window at the remote aspect ratio, as large as fits the work area.
            var dpi = VisualTreeHelper.GetDpi(this);
            bool turned = (_session.Host.ViewRotation & 1) != 0;
            double w = (turned ? f.Height : f.Width) / dpi.DpiScaleX, h = (turned ? f.Width : f.Height) / dpi.DpiScaleY;
            var area = SystemParameters.WorkArea;
            double scale = Math.Min(1, Math.Min((area.Width - 40) / w, (area.Height - 80 - Toolbar.Height) / h));
            Width = Math.Max(MinWidth, w * scale + 16);
            Height = Math.Max(MinHeight, h * scale + Toolbar.Height + 39);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
    }

    /// <summary>Recomputes where the picture goes (window size, fit / 1:1, pan) and hands it to the renderer.</summary>
    private void UpdateView()
    {
        var f = _format;
        int vw = f?.Width ?? 0, vh = f?.Height ?? 0;
        var layout = ViewLayout.Compute(Video.PixelWidth, Video.PixelHeight, vw, vh, FitToggle.IsChecked == true, _panX, _panY, _session.Host.ViewRotation);
        if (layout == _layout) return;
        _layout = layout;
        _video.SetLayout(layout);
    }

    private readonly Dictionary<ulong, nint> _cursors = new();
    private static readonly nint Arrow = LoadCursorW(0, 32512);

    /// <summary>Remote cursors repeat constantly (arrow, I-beam…): build each native cursor once.</summary>
    private nint GetCursor(byte kind, int hx, int hy, int w, int h, byte[] data)
    {
        if (kind == 0) return 0;
        ulong key = System.IO.Hashing.XxHash64.HashToUInt64(data, ((long)kind << 48) | ((long)hx << 32) | ((long)hy << 16) | (uint)w);
        if (_cursors.TryGetValue(key, out var c)) return c;
        if (_cursors.Count > 64)
        {
            var current = Video.CursorHandle;
            foreach (var old in _cursors.Values) if (old != current) Native.DestroyIcon(old);
            _cursors.Clear();
        }
        c = CursorFactory.Create(kind, hx, hy, w, h, data);
        if (c == 0) return Arrow;
        _cursors[key] = c;
        return c;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint name);

    private void UpdateStats()
    {
        long bytes = _session.BytesReceived;
        double mbps = Math.Max(0, bytes - _lastBytes) * 8 / 1e6;
        _lastBytes = bytes;
        int shown = _video.FramesShown;
        int fps = Math.Max(0, shown - _lastShown);
        _lastShown = shown;
        if (!_session.IsConnected) { Stats.Text = ""; return; }
        var f = _format;
        Stats.Text = $"{fps} fps · {mbps:0.0} Mbps · {_session.RttMs:0.0} ms{(_session.ViaInternet ? " · Internet" : "")}";
        Stats.ToolTip = f is null ? null :
            $"{f.Width}×{f.Height} · {ShortEncoder(f.Encoder)}{(f.Chroma444 ? " 4:4:4" : " 4:2:0")}{(f.GpuCapture ? " · captura en GPU" : "")}\n" +
            $"Tasa de bits del host {_session.HostBitrateKbps / 1000.0:0.0} Mbps{(_session.Options.AdaptiveBitrate ? " (automática)" : "")} · cola {_session.HostQueueMs:0} ms · codificación {_session.HostEncodeMs:0.0} ms\n" +
            $"Decodificación {_video.DecoderInfo}, {_video.DecodeMs:0.0} ms · latencia de red (ida y vuelta) {_session.RttMs:0.0} ms\n" +
            (_session.ViaInternet ? "Conexión por Internet (P2P, QUIC cifrado, un flujo por canal)" : "Conexión directa en la red local (TLS)");
    }

    private static string ShortEncoder(string e) => e switch
    {
        "h264_nvenc" => "NVENC H.264", "hevc_nvenc" => "NVENC HEVC", "av1_nvenc" => "NVENC AV1",
        "h264_qsv" => "QuickSync H.264", "hevc_qsv" => "QuickSync HEVC", "av1_qsv" => "QuickSync AV1",
        "h264_amf" => "AMF H.264", "hevc_amf" => "AMF HEVC", "av1_amf" => "AMF AV1",
        "libx264" => "x264", "h264_mf" => "MF H.264", "hevc_mf" => "MF HEVC", _ => e,
    };

    private void ShowToast(string text, double seconds, double? progress = null)
    {
        _toastWanted = true;
        ToastText.Text = text;
        ToastProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is not null) ToastProgress.Value = progress.Value;
        if (IsActive || !IsLoaded) ToastPopup.IsOpen = true;
        PlaceToast();
        _toastTimer?.Stop();
        if (seconds > 0)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _toastTimer.Tick += (_, _) => { _toastWanted = false; ToastPopup.IsOpen = false; _toastTimer?.Stop(); };
            _toastTimer.Start();
        }
    }
    private DispatcherTimer? _toastTimer;

    /// <summary>Bottom-right corner of the window (a popup does not follow its owner by itself).</summary>
    private void PlaceToast()
    {
        if (!ToastPopup.IsOpen) return;
        Toast.Measure(new Size(420, double.PositiveInfinity));
        var size = Toast.DesiredSize;
        ToastPopup.HorizontalOffset = Math.Max(0, RootGrid.ActualWidth - size.Width - 16);
        ToastPopup.VerticalOffset = Math.Max(0, RootGrid.ActualHeight - size.Height - 16);
        // A popup only moves when its offsets change; nudge it so it follows the window when it moves.
        ToastPopup.HorizontalOffset += 0.01;
        ToastPopup.HorizontalOffset -= 0.01;
    }

    // ---------------- toolbar ----------------

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || MonitorBox.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        var o = _session.Options;
        if (o.MonitorIndex == index) return;
        o.MonitorIndex = index;
        _session.SetOptions(o);
        _sizedToRemote = false;
    }

    private void OnStreamOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || !IsInitialized) return;
        var o = _session.Options;
        if (BitrateBox.SelectedItem is ComboBoxItem { Tag: int b }) o.BitrateMbps = b;
        if (FpsBox.SelectedItem is ComboBoxItem { Tag: int f }) o.MaxFps = f;
        if (CodecBox.SelectedItem is ComboBoxItem { Tag: string codec }) o.Codec = codec;
        o.AdaptiveBitrate = AdaptiveToggle.IsChecked == true;
        o.Chroma444 = Chroma444.IsChecked == true;
        o.Audio = AudioToggle.IsChecked == true;
        _audioEnabled = o.Audio;
        if (!o.Audio) _audio.Dispose();
        _session.SetOptions(o);
        _settings.Defaults = new StreamOptions
        {
            MaxFps = o.MaxFps, BitrateMbps = o.BitrateMbps, AdaptiveBitrate = o.AdaptiveBitrate, Chroma444 = o.Chroma444, Audio = o.Audio, Codec = o.Codec,
        };
        _settings.Save();
    }

    private void OnFitChanged(object sender, RoutedEventArgs e)
    {
        _settings.FitToWindow = FitToggle.IsChecked == true;
        _settings.Save();
        ApplyFit();
    }

    private void ApplyFit()
    {
        bool fit = FitToggle.IsChecked == true;
        FitToggle.Content = fit ? "Ajustar" : "1:1";
        FitToggle.ToolTip = fit ? "Ajustar a la ventana / tamaño real 1:1 píxel"
            : "Tamaño real 1:1 píxel; si el remoto es más grande que la ventana, la vista sigue al ratón";
        UpdateView();
    }

    private void OnKeysChanged(object sender, RoutedEventArgs e)
    {
        _settings.CaptureSystemKeys = KeysToggle.IsChecked == true;
        _settings.Save();
        if (_hook is not null) _hook.Enabled = _settings.CaptureSystemKeys;
    }

    private void OnRotateMenu(object sender, RoutedEventArgs e)
    {
        int r = _session.Host.ViewRotation & 3;
        Rotate0.IsChecked = r == 0; Rotate1.IsChecked = r == 1; Rotate2.IsChecked = r == 2; Rotate3.IsChecked = r == 3;
        var menu = RotateButton.ContextMenu!;
        menu.PlacementTarget = RotateButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnRotate(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out int r)) return;
        _session.Host.ViewRotation = r;
        _settings.Save(); // remembered per remote machine
        _sizedToRemote = false;
        if (_format is { } f) OnVideoFormat(f); // reshape the window to the new aspect ratio
        else UpdateView();
    }

    private void OnActions(object sender, RoutedEventArgs e)
    {
        var menu = ActionsButton.ContextMenu!;
        menu.PlacementTarget = ActionsButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnReleaseKeys(object sender, RoutedEventArgs e) => ReleaseAllInput();
    private void OnKeyframe(object sender, RoutedEventArgs e) => _session.RequestKeyframe();

    private void OnCtrlAltDel(object sender, RoutedEventArgs e) => _session.Special(SpecialAction.CtrlAltDel);
    private void OnTaskManager(object sender, RoutedEventArgs e) => _session.Special(SpecialAction.TaskManager);
    private void OnWinKey(object sender, RoutedEventArgs e) => _session.Special(SpecialAction.WinKey);
    private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void OnDisconnect(object sender, RoutedEventArgs e) => Close();

    private void OnSendFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Enviar archivos al equipo remoto" };
        if (dlg.ShowDialog(this) == true) _ = Upload(dlg.FileNames);
    }

    private RemoteFilesWindow? _filesWindow;
    private RemoteDisplayWindow? _displayWindow;

    private void OnRemoteDisplay(object sender, RoutedEventArgs e)
    {
        if (!_session.IsConnected) { ShowToast("No hay conexión.", 3); return; }
        if (_session.Info?.DisplayControl != true)
        {
            ShowToast("El equipo remoto necesita RemoteConnect 2.3 o posterior para cambiar su resolución y escala.", 8);
            return;
        }
        if (_displayWindow is { IsLoaded: true }) { _displayWindow.Activate(); return; }
        _displayWindow = new RemoteDisplayWindow(_session, () => (_session.Host.ViewRotation & 1) != 0
            ? (Video.PixelHeight, Video.PixelWidth) : (Video.PixelWidth, Video.PixelHeight)) { Owner = this };
        _displayWindow.Closed += (_, _) => _displayWindow = null;
        _displayWindow.Show();
    }

    private void OnReceiveFiles(object sender, RoutedEventArgs e)
    {
        if (!_session.IsConnected) { ShowToast("No hay conexión.", 3); return; }
        if (_session.Info?.FileDownload != true)
        {
            ShowToast("El equipo remoto no permite descargar archivos (necesita RemoteConnect 2.1 o posterior y tener la opción activada).", 8);
            return;
        }
        if (_filesWindow is { IsLoaded: true }) { _filesWindow.Activate(); return; }
        _filesWindow = new RemoteFilesWindow(_session, _settings) { Owner = this };
        _filesWindow.Closed += (_, _) => _filesWindow = null;
        _filesWindow.Show();
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        var pwd = RetryPassword.Password;
        if (RememberRetry.IsChecked == true) { _session.Host.SetPassword(pwd); _settings.Save(); }
        ShowOverlay("Conectando…", true, false);
        _session.Retry(pwd);
        ScreenArea.Focus();
    }

    private void OnRetryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRetry(sender, e);
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _prevState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // force re-layout over the taskbar
            WindowState = WindowState.Maximized;
            _fullScreen = true;
            SetToolbar(false);
            FullButton.Content = ""; // BackToWindow glyph
            ShowToast("Pantalla completa. Ctrl+Alt+Intro para salir; lleva el ratón al borde superior para ver la barra.", 4);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _prevState;
            _fullScreen = false;
            SetToolbar(true);
            FullButton.Content = ""; // FullScreen glyph
        }
        ScreenArea.Focus();
    }

    /// <summary>The toolbar can't overlap the native video window, so the video moves down while it shows.</summary>
    private void SetToolbar(bool visible)
    {
        Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ScreenArea.Margin = new Thickness(0, visible ? Toolbar.Height : 0, 0, 0);
        Overlay.Margin = ScreenArea.Margin;
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e) => CheckToolbar(e.GetPosition(this).Y);

    /// <summary>Full screen: mouse at the top edge shows the toolbar, moving away hides it.</summary>
    private void CheckToolbar(double y)
    {
        if (!_fullScreen) return;
        if (y <= 2 && Toolbar.Visibility != Visibility.Visible) SetToolbar(true);
        else if (y > Toolbar.Height + 30 && Toolbar.Visibility == Visibility.Visible && !Toolbar.IsMouseOver && !AnyDropDownOpen())
            SetToolbar(false);
    }

    private bool AnyDropDownOpen() => MonitorBox.IsDropDownOpen || BitrateBox.IsDropDownOpen || FpsBox.IsDropDownOpen || CodecBox.IsDropDownOpen || ActionsButton.ContextMenu!.IsOpen;

    // ---------------- input ----------------

    private void OnVideoMouseMove(int x, int y)
    {
        if (_fullScreen)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            CheckToolbar(y / dpi.DpiScaleY + ScreenArea.Margin.Top);
        }
        // 1:1 with a remote screen larger than the window: the view follows the mouse.
        if (FitToggle.IsChecked != true && Video.PixelWidth > 0 && Video.PixelHeight > 0)
        {
            _panX = Math.Clamp((double)x / Video.PixelWidth, 0, 1);
            _panY = Math.Clamp((double)y / Video.PixelHeight, 0, 1);
            UpdateView();
        }
        if (!_session.IsConnected || _layout is null) return;
        var (nx, ny) = _layout.Normalize(x, y);
        _session.MouseMove(nx, ny);
    }

    private void OnVideoButton(MouseButtonId id, bool down, int x, int y)
    {
        if (down) ScreenArea.Focus();
        if (!_session.IsConnected || _layout is null) return;
        if (down)
        {
            var (nx, ny) = _layout.Normalize(x, y);
            _session.MouseMove(nx, ny);
            _buttonsDown |= 1 << (int)id;
        }
        else _buttonsDown &= ~(1 << (int)id);
        _session.MouseButton(id, down);
    }

    private void ReleaseButtons()
    {
        for (int b = 0; b < 5; b++)
            if ((_buttonsDown & (1 << b)) != 0) _session.MouseButton((MouseButtonId)b, false);
        _buttonsDown = 0;
    }

    /// <summary>Wheel messages that went to the WPF window (focus) while the pointer is over the video.</summary>
    private void OnWpfWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_session.IsConnected || !Video.CursorOver()) return;
        _session.MouseWheel(e.Delta, false);
        e.Handled = true;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEHWHEEL && _session.IsConnected && Video.CursorOver())
        {
            _session.MouseWheel((short)((long)wParam >> 16), true);
            handled = true;
        }
        return 0;
    }

    /// <summary>Low-level hook path (system keys captured).</summary>
    private bool OnHookKey(ushort vk, ushort scan, KeyFlags flags)
    {
        if (!_session.IsConnected || Overlay.Visibility == Visibility.Visible) return false;
        SendKey(vk, scan, flags);
        return true;
    }

    /// <summary>WPF path (system keys not captured): virtual keys only.</summary>
    private void OnWpfKey(object sender, KeyEventArgs e)
    {
        if (_hook?.Enabled == true || !_session.IsConnected || Overlay.Visibility == Visibility.Visible) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        bool ext = key is Key.RightCtrl or Key.RightAlt or Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp
            or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.NumLock or Key.Divide or Key.PrintScreen;
        SendKey((ushort)vk, 0, (e.IsDown ? KeyFlags.Down : 0) | (ext ? KeyFlags.Extended : 0));
        e.Handled = true;
    }

    private void SendKey(ushort vk, ushort scan, KeyFlags flags)
    {
        var id = (vk, scan, flags & KeyFlags.Extended);
        if (flags.HasFlag(KeyFlags.Down)) _keysDown.Add(id); else _keysDown.Remove(id);
        _session.Key(vk, scan, flags);
    }

    private void ReleaseAllInput()
    {
        foreach (var (vk, scan, ext) in _keysDown.ToList()) _session.Key(vk, scan, ext);
        _keysDown.Clear();
        ReleaseButtons();
        if (_session.IsConnected) _session.Special(SpecialAction.ReleaseAllKeys);
    }

    // ---------------- files ----------------

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = Upload(paths);
    }

    private async Task Upload(string[] paths)
    {
        if (!_session.IsConnected) { ShowToast("No hay conexión.", 3); return; }
        var files = new List<(string, string)>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var parent = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    files.Add((f, Path.GetRelativePath(parent, f)));
            }
            else if (File.Exists(p)) files.Add((p, Path.GetFileName(p)));
        }
        if (files.Count == 0) return;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<(string Name, long Done, long Total)>(x =>
        {
            double mbs = x.Done / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            ShowToast($"Enviando {x.Name}\n{x.Done / 1e6:0.0} / {x.Total / 1e6:0.0} MB · {mbs:0} MB/s", 0, x.Total == 0 ? 1 : (double)x.Done / x.Total);
        });
        try
        {
            await _session.UploadAsync(files, progress);
            ShowToast($"{files.Count} archivo(s) enviados a la carpeta de descargas remota{(_lastSavedPath is null ? "" : $":\n{Path.GetDirectoryName(_lastSavedPath)}")}", 6);
        }
        catch (Exception ex)
        {
            ShowToast("Error al enviar: " + ex.Message, 8);
        }
    }
}
