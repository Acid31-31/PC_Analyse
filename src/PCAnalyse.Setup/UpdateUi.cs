using System.Windows;
using PCAnalyse.Core;

namespace PCAnalyse.Setup;

public static class UpdateUi
{
    public static bool TryHandleApplyUpdate(UpdateChannel channel, string[] args)
    {
        if (!UpdateApplyRunner.TryParse(args, out var staged, out var target, out var pid))
            return false;

        var window = new UpdateApplyProgressWindow(channel, staged, target, pid);
        System.Windows.Application.Current.MainWindow = window;
        window.Show();
        return true;
    }

    public static async Task CheckAsync(Window owner, UpdateChannel channel, bool showIfCurrent)
    {
        AppUpdateInfo update;
        try
        {
            update = await GitHubUpdateService.CheckForUpdateAsync(channel);
        }
        catch (Exception ex)
        {
            if (showIfCurrent)
                System.Windows.MessageBox.Show(owner, ex.Message, "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (update.UpdateAvailable)
        {
            var dialog = new UpdateAvailableWindow(channel, update)
            {
                Owner = owner
            };
            dialog.ShowDialog();
            return;
        }

        if (!showIfCurrent)
            return;

        if (!string.IsNullOrWhiteSpace(update.ErrorMessage))
        {
            System.Windows.MessageBox.Show(owner, update.ErrorMessage, "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var remote = string.IsNullOrWhiteSpace(update.ReleaseTag) ? "unbekannt" : update.ReleaseTag;
        System.Windows.MessageBox.Show(
            owner,
            $"{channel.ProductName} {AppInfo.DisplayVersion} ist aktuell.\nGitHub: {remote}",
            "Update",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
