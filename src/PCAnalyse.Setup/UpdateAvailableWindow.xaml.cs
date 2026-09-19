using System.Windows;
using System.Windows.Input;
using PCAnalyse.Core;

namespace PCAnalyse.Setup;

public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateChannel _channel;
    private readonly AppUpdateInfo _update;
    private bool _isUpdating;
    private CancellationTokenSource? _updateCts;

    public UpdateAvailableWindow(UpdateChannel channel, AppUpdateInfo update)
    {
        InitializeComponent();
        _channel = channel;
        _update = update;
        Owner = System.Windows.Application.Current?.MainWindow;

        var sizeText = _update.AssetSizeBytes > 0 ? _update.AssetSizeDisplay : "";
        VersionTextBlock.Text = $"Installiert: {AppInfo.DisplayVersion}   →   Neu: {_update.ReleaseTag}";
        SizeTextBlock.Text = string.IsNullOrWhiteSpace(sizeText)
            ? "Update-Größe: wird beim Download angezeigt"
            : "Update-Größe: " + sizeText;
        NotesTextBlock.Text = FormatNotes(_update.ReleaseNotes);
        Closing += UpdateAvailableWindow_Closing;
    }

    private static string FormatNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return "Keine Release-Notizen.";
        var text = notes.Trim().Trim('\uFEFF');
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line =>
        {
            if (line.StartsWith("### ", StringComparison.Ordinal)) return line[4..].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal)) return line[3..].Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                return "• " + line[2..].Trim();
            return line;
        })).Trim();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
            return;

        _isUpdating = true;
        _updateCts = new CancellationTokenSource();
        UpdateButton.IsEnabled = false;
        LaterButton.Content = "Abbrechen – später";
        NotesBorder.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "Update wird vorbereitet…";

        try
        {
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                UpdateProgressBar.Value = info.Percent;
                PercentTextBlock.Text = info.Percent + " %";
                StatusTextBlock.Text = info.Message;
                if (info.TotalBytes > 0)
                {
                    var text = AppUpdateInfo.FormatMegabytes(info.BytesRead)
                        + " von " + AppUpdateInfo.FormatMegabytes(info.TotalBytes);
                    if (info.BytesPerSecond > 0)
                        text += "  ·  " + AppUpdateInfo.FormatMegabytes(info.BytesPerSecond) + "/s";
                    DownloadSizeTextBlock.Text = text;
                }
                else if (info.BytesRead > 0)
                    DownloadSizeTextBlock.Text = AppUpdateInfo.FormatMegabytes(info.BytesRead) + " geladen";
            });

            var stagedRoot = await GitHubUpdateService.DownloadAndStageUpdateAsync(
                _channel, _update, progress, _updateCts.Token);
            StatusTextBlock.Text = "Installation wird gestartet…";
            GitHubUpdateService.LaunchUpdaterAndShutdown(_channel, stagedRoot);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            UpdateButton.IsEnabled = true;
            LaterButton.Content = "Später";
            NotesBorder.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
            System.Windows.MessageBox.Show(this, ex.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            _updateCts?.Cancel();
            LaterButton.IsEnabled = false;
            StatusTextBlock.Text = "Update wird abgebrochen…";
            return;
        }

        DialogResult = false;
        Close();
    }

    private void UpdateAvailableWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isUpdating)
            return;
        e.Cancel = true;
        _updateCts?.Cancel();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* ignorieren */ }
    }
}
