using System.Windows;
using System.Windows.Input;
using PCAnalyse.Core;
using PCAnalyse.InputShare;
using PCAnalyse.Setup;

namespace PCAnalyse.Verbindung;

public partial class MainWindow : Window
{
    private readonly PairingStore _store = new();
    private LinkHub? _hub;
    private MouseShareService? _mouseShare;
    private DeskShareService? _deskShare;

    public MainWindow()
    {
        InitializeComponent();
        MachineText.Text = Environment.MachineName;
        Loaded += async (_, _) =>
        {
            StartWaiting();
            try { await UpdateUi.CheckAsync(this, UpdateChannel.Verbindung, showIfCurrent: false); }
            catch { /* stiller Start */ }
        };
    }

    private void StartWaiting()
    {
        _hub = new LinkHub(_store) { Waiting = true };
        _hub.StatusChanged += text => Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            WaitText.Text = text;
        });
        _hub.PeerConnected += (_, name) => Dispatcher.Invoke(() =>
        {
            WaitBar.IsIndeterminate = false;
            WaitBar.Visibility = Visibility.Collapsed;
            WaitText.Text = "Verbunden mit " + name;
            StatusText.Text = "Verbunden mit " + name;
            StartMouseShare();
            StartDeskShare();
        });
        _hub.PeersChanged += peers => Dispatcher.Invoke(() =>
        {
            if (_hub?.ConnectedPeer is not null || peers.Count == 0)
                return;
            WaitText.Text = "Gefunden: " + peers[0].ComputerName + " – verbindet …";
        });
        _hub.Start();
        StatusText.Text = _hub.Status;
        WaitText.Text = _hub.Status;
    }

    private void StartMouseShare()
    {
        if (_hub?.PeerAddress is null)
            return;
        if (_mouseShare is null)
        {
            _mouseShare = new MouseShareService();
            _mouseShare.StatusChanged += text => Dispatcher.Invoke(() =>
            {
                MouseShareText.Text = text;
                StatusText.Text = text;
            });
        }
        _mouseShare.Start(_hub.PeerAddress, _hub.PeerInstanceId, _hub.InstanceId, PeerSide.Left);
        MouseShareText.Text = _mouseShare.Status;
    }

    private void StartDeskShare()
    {
        if (_hub?.PeerAddress is null)
            return;
        if (_deskShare is null)
        {
            _deskShare = new DeskShareService();
            _deskShare.StatusChanged += text => Dispatcher.Invoke(() =>
            {
                if (DeskShareText is not null)
                    DeskShareText.Text = text;
                StatusText.Text = text;
            });
        }

        _deskShare.Start(_hub.PeerAddress, _hub.PeerInstanceId, _hub.InstanceId);
        if (DeskShareText is not null)
            DeskShareText.Text = _deskShare.Status;
    }

    private void SwitchMouse_Click(object sender, RoutedEventArgs e)
    {
        _mouseShare?.SwitchNow();
    }

    private void ScreenshotPeer_Click(object sender, RoutedEventArgs e) => _deskShare?.RequestScreenshot();

    private void PullClipboard_Click(object sender, RoutedEventArgs e) => _deskShare?.PullClipboard();

    private void OpenDropFolder_Click(object sender, RoutedEventArgs e) => DeskShareService.OpenDropFolder();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _deskShare?.SendDropped(paths);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Suche GitHub-Update …";
        await UpdateUi.CheckAsync(this, UpdateChannel.Verbindung, showIfCurrent: true);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* ignorieren */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, EventArgs e)
    {
        _mouseShare?.Dispose();
        _deskShare?.Dispose();
        _hub?.Dispose();
    }
}
