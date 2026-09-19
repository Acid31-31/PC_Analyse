using System.Windows;
using PCAnalyse.Core;

namespace PCAnalyse.Setup;

public partial class UpdateApplyProgressWindow : Window
{
    private readonly UpdateChannel _channel;
    private readonly string _stagedRoot;
    private readonly string _targetRoot;
    private readonly int _parentProcessId;
    private CancellationTokenSource? _cts;
    private bool _isApplying;

    public UpdateApplyProgressWindow(UpdateChannel channel, string stagedRoot, string targetRoot, int parentProcessId)
    {
        InitializeComponent();
        _channel = channel;
        _stagedRoot = stagedRoot;
        _targetRoot = targetRoot;
        _parentProcessId = parentProcessId;
        Title = channel.ProductName + " wird aktualisiert";
        Loaded += UpdateApplyProgressWindow_Loaded;
        Closing += UpdateApplyProgressWindow_Closing;
    }

    private async void UpdateApplyProgressWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= UpdateApplyProgressWindow_Loaded;
        _cts = new CancellationTokenSource();
        _isApplying = true;
        var progress = new Progress<UpdateProgressInfo>(info =>
        {
            InstallProgressBar.Value = info.Percent;
            PercentTextBlock.Text = info.Percent + " %";
            StatusTextBlock.Text = info.Message;
        });

        try
        {
            await Task.Run(() => UpdateApplyRunner.ApplyUpdate(
                _channel, _stagedRoot, _targetRoot, progress, _cts.Token, _parentProcessId), _cts.Token);
            _isApplying = false;
            System.Windows.Application.Current?.Shutdown();
        }
        catch (OperationCanceledException)
        {
            _isApplying = false;
            System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            _isApplying = false;
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message, "Update fehlgeschlagen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void UpdateApplyProgressWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isApplying)
            return;
        try { _cts?.Cancel(); } catch { /* ignorieren */ }
        _isApplying = false;
    }

    private void RequestCancel()
    {
        try { _cts?.Cancel(); } catch { /* ignorieren */ }
        CancelButton.IsEnabled = false;
        StatusTextBlock.Text = "Update wird abgebrochen…";
    }
}
