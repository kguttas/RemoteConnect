using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteConnect.Client;
using RemoteConnect.Core;

namespace RemoteConnect.UI;

/// <summary>Browses the remote machine's folders and downloads files / folders from it to this one.</summary>
public partial class RemoteFilesWindow : Window
{
    public sealed record Row(RemoteEntry Entry, string Icon, Brush IconBrush, string Name, string SizeText, string ModifiedText);

    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39));
    private static readonly Brush FileBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x9B, 0xB0));

    private readonly RemoteSession _session;
    private readonly ClientSettings _settings;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _path = "";
    private string? _parent;
    private bool _downloading;
    private long _totalBytes;
    private int _totalFiles, _doneFiles, _failedFiles;
    private Stopwatch _clock = new();
    private string? _lastFile;

    public RemoteFilesWindow(RemoteSession session, ClientSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;
        _session.DownloadFolder = settings.DownloadFolder;
        FolderText.Text = settings.DownloadFolder;
        Title = $"Archivos de {session.Host.Name}";

        _session.DirListing += OnListing;
        _session.DownloadState += OnDownloadState;
        _session.FileDownloaded += OnFileDownloaded;
        _progressTimer.Tick += (_, _) => ShowProgress();
        Closed += (_, _) =>
        {
            _session.DirListing -= OnListing;
            _session.DownloadState -= OnDownloadState;
            _session.FileDownloaded -= OnFileDownloaded;
            _progressTimer.Stop();
            if (_downloading) _session.CancelDownload();
        };
        Loaded += (_, _) => Navigate("");
    }

    // ---------------- browsing ----------------

    private void Navigate(string path)
    {
        if (!_session.IsConnected) { StatusText.Text = "Sin conexión con el equipo remoto."; return; }
        StatusText.Text = "Cargando…";
        _session.ListRemote(path);
    }

    private void OnListing(DirListingInfo info) => Dispatcher.BeginInvoke(() =>
    {
        if (info.Error is not null && info.Entries.Count == 0)
        {
            // Stay where we were, say why.
            StatusText.Text = info.Error;
            if (List.Items.Count == 0) ShowEmpty(info.Error);
            return;
        }
        _path = info.Path;
        _parent = info.Parent;
        PathBox.Text = info.Path.Length == 0 ? "Este equipo remoto" : info.Path;
        UpButton.IsEnabled = _parent is not null;
        List.ItemsSource = info.Entries.Select(ToRow).ToList();
        ShowEmpty(info.Entries.Count == 0 ? "Carpeta vacía" : null);
        int dirs = info.Entries.Count(e => e.IsDir);
        if (!_downloading)
            StatusText.Text = info.Path.Length == 0 ? "Elige una unidad o carpeta. Doble clic para abrir; selecciona y pulsa Descargar."
                : $"{dirs} carpeta(s), {info.Entries.Count - dirs} archivo(s)";
    });

    private void ShowEmpty(string? text)
    {
        EmptyText.Text = text ?? "";
        EmptyText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Row ToRow(RemoteEntry e) => new(e,
        e.IsDir ? (e.Path.Length <= 3 ? "" : "") : "",
        e.IsDir ? FolderBrush : FileBrush,
        e.Name,
        e.IsDir ? (e.Path.Length <= 3 && e.Size > 0 ? $"{FormatBytes(e.Size)} usados" : "") : FormatBytes(e.Size),
        e.Modified == default ? "" : e.Modified.ToString("g"));

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };

    private void OnUp(object sender, RoutedEventArgs e) { if (_parent is not null) Navigate(_parent); }
    private void OnHome(object sender, RoutedEventArgs e) => Navigate("");
    private void OnRefresh(object sender, RoutedEventArgs e) => Navigate(_path);

    private void OnPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = PathBox.Text.Trim();
        Navigate(text == "Este equipo remoto" ? "" : text);
        e.Handled = true;
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Row row) return;
        if (row.Entry.IsDir) Navigate(row.Entry.Path);
        else StartDownload([row.Entry]);
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { OnUp(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && List.SelectedItem is Row { Entry.IsDir: true } row && List.SelectedItems.Count == 1)
        {
            Navigate(row.Entry.Path);
            e.Handled = true;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rows = List.SelectedItems.Cast<Row>().ToList();
        DownloadButton.IsEnabled = rows.Count > 0 && !_downloading;
        DownloadButton.Content = rows.Count switch
        {
            0 => "Descargar",
            1 => rows[0].Entry.IsDir ? "Descargar carpeta" : "Descargar",
            _ => $"Descargar {rows.Count} elementos",
        };
    }

    // ---------------- downloading ----------------

    private void OnDownload(object sender, RoutedEventArgs e) => StartDownload(List.SelectedItems.Cast<Row>().Select(r => r.Entry).ToList());

    private void StartDownload(List<RemoteEntry> entries)
    {
        if (_downloading || entries.Count == 0) return;
        // Drive roots: downloading a whole disk is almost certainly a misclick.
        if (entries.Any(x => x.Path.Length <= 3))
        {
            StatusText.Text = "No se puede descargar una unidad entera: abre la unidad y elige carpetas o archivos.";
            return;
        }
        try { _session.Download(entries.Select(x => x.Path)); }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; return; }
        _downloading = true;
        _totalBytes = 0; _totalFiles = 0; _doneFiles = 0; _failedFiles = 0; _lastFile = null;
        _clock = Stopwatch.StartNew();
        DownloadButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Preparando la descarga…";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_downloading) return;
        _session.CancelDownload();
        StatusText.Text = "Cancelando…";
    }

    private void OnDownloadState(DownloadInfo info) => Dispatcher.BeginInvoke(() =>
    {
        switch (info.State)
        {
            case DownloadInfo.Phase.Started:
                _totalBytes = info.Bytes;
                _totalFiles = info.Files;
                Progress.IsIndeterminate = false;
                _progressTimer.Start();
                ShowProgress();
                break;
            case DownloadInfo.Phase.Finished:
                Done(_failedFiles == 0
                    ? $"{_doneFiles} archivo(s) descargados ({FormatBytes(_session.DownloadedBytes)}) en {_clock.Elapsed.TotalSeconds:0.0} s."
                    : $"{_doneFiles} archivo(s) descargados, {_failedFiles} con error.");
                break;
            case DownloadInfo.Phase.Cancelled:
                Done("Descarga cancelada (los archivos a medias se han borrado).");
                break;
            case DownloadInfo.Phase.Failed:
                Done("Error en la descarga: " + (info.Error ?? "desconocido"));
                break;
        }
    });

    private void OnFileDownloaded(FileAckInfo ack) => Dispatcher.BeginInvoke(() =>
    {
        if (ack.Ok) { _doneFiles++; _lastFile = ack.Path; }
        else _failedFiles++;
    });

    private void ShowProgress()
    {
        if (!_downloading || _totalBytes < 0) return;
        long done = _session.DownloadedBytes;
        double secs = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        Progress.Value = _totalBytes == 0 ? 1 : Math.Min(1, (double)done / _totalBytes);
        StatusText.Text = $"Descargando {_doneFiles}/{_totalFiles} · {FormatBytes(done)} de {FormatBytes(_totalBytes)} · {done / 1e6 / secs:0.0} MB/s";
    }

    private void Done(string text)
    {
        _downloading = false;
        _progressTimer.Stop();
        Progress.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        OnSelectionChanged(this, null!);
    }

    // ---------------- destination ----------------

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadFolder);
            // Select the last file when there is one, otherwise just open the folder.
            var args = _lastFile is not null && File.Exists(_lastFile) ? $"/select,\"{_lastFile}\"" : $"\"{_settings.DownloadFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta para los archivos descargados", InitialDirectory = _settings.DownloadFolder };
        if (dlg.ShowDialog(this) != true) return;
        _settings.DownloadFolder = dlg.FolderName;
        _settings.Save();
        _session.DownloadFolder = dlg.FolderName;
        FolderText.Text = dlg.FolderName;
    }
}
