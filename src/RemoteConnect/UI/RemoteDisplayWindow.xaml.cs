using System.Windows;
using System.Windows.Controls;
using RemoteConnect.Client;
using RemoteConnect.Core;

namespace RemoteConnect.UI;

/// <summary>Picks the remote monitor's resolution and Windows scale for the session.</summary>
public partial class RemoteDisplayWindow : Window
{
    private readonly RemoteSession _session;
    private readonly Func<(int W, int H)> _viewSize;
    private DisplayInfo? _info;
    private bool _busy;

    /// <param name="viewSize">The client's picture area in physical pixels, for «Ajustar a esta ventana».</param>
    public RemoteDisplayWindow(RemoteSession session, Func<(int W, int H)> viewSize)
    {
        InitializeComponent();
        _session = session;
        _viewSize = viewSize;
        _session.DisplayInfo += OnInfo;
        Closed += (_, _) => _session.DisplayInfo -= OnInfo;
        Loaded += (_, _) => _session.QueryDisplay();
    }

    private void OnInfo(DisplayInfo info) => Dispatcher.BeginInvoke(() =>
    {
        _info = info;
        _busy = false;
        CurrentText.Text = $"Ahora: {info.Width} × {info.Height}" + (info.Scale > 0 ? $" al {info.Scale} %" : "") +
            (info.RecommendedScale > 0 ? $" (recomendada {info.RecommendedScale} %)" : "");
        ModeBox.ItemsSource = info.Modes;
        ModeBox.SelectedItem = info.Modes.FirstOrDefault(m => m.Width == info.Width && m.Height == info.Height);
        ScaleBox.ItemsSource = info.Scales.Select(s => new ComboBoxItem { Content = s == info.RecommendedScale ? $"{s} % (recomendada)" : $"{s} %", Tag = s }).ToList();
        ScaleBox.SelectedItem = ((List<ComboBoxItem>)ScaleBox.ItemsSource).FirstOrDefault(i => (int)i.Tag == info.Scale);
        ModeBox.IsEnabled = info.Modes.Count > 0;
        ScaleBox.IsEnabled = info.Scales.Count > 0;
        FitButton.IsEnabled = info.Modes.Count > 0;
        ApplyButton.IsEnabled = ModeBox.IsEnabled || ScaleBox.IsEnabled;
        RestoreButton.IsEnabled = info.Changed;
        StatusText.Text = info.Error is not null ? "Error: " + info.Error
            : info.Changed ? $"Original: {info.OriginalWidth} × {info.OriginalHeight}{(info.OriginalScale > 0 ? $" al {info.OriginalScale} %" : "")}. Se restaura solo al desconectar."
            : "Los cambios se deshacen solos al desconectar.";
    });

    private void OnFit(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        var (w, h) = _viewSize();
        // The largest mode that fits the window (shown 1:1 or larger); if none fits, the smallest one.
        var fit = _info.Modes.Where(m => m.Width <= w && m.Height <= h).OrderByDescending(m => (long)m.Width * m.Height).FirstOrDefault()
                  ?? _info.Modes.OrderBy(m => (long)m.Width * m.Height).FirstOrDefault();
        if (fit is not null) ModeBox.SelectedItem = fit;
        var hundred = ((IEnumerable<ComboBoxItem>?)ScaleBox.ItemsSource)?.FirstOrDefault(i => (int)i.Tag == 100);
        if (hundred is not null) ScaleBox.SelectedItem = hundred;
        StatusText.Text = $"Ventana: {w} × {h}. Pulsa Aplicar.";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_info is null || _busy) return;
        var mode = ModeBox.SelectedItem as DisplayMode;
        int scale = ScaleBox.SelectedItem is ComboBoxItem { Tag: int s } ? s : 0;
        Send(() => _session.SetDisplay(mode?.Width ?? 0, mode?.Height ?? 0, scale));
    }

    private void OnRestore(object sender, RoutedEventArgs e) => Send(_session.RestoreDisplay);

    private void Send(Action action)
    {
        if (!_session.IsConnected) { StatusText.Text = "Sin conexión con el equipo remoto."; return; }
        _busy = true;
        ApplyButton.IsEnabled = RestoreButton.IsEnabled = false;
        StatusText.Text = "Aplicando… (la imagen se corta un momento)";
        action();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
