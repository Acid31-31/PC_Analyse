using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace PCAnalyse.Setup;

public partial class InstallWizardWindow : Window
{
    private readonly ProductDefinition _product;
    private readonly bool _uninstall;
    private int _step;

    public InstallWizardWindow(ProductDefinition product, bool uninstall = false)
    {
        _product = product;
        _uninstall = uninstall;
        InitializeComponent();
        Title = product.ProductName + " Setup";
        HeaderTitle.Text = product.ProductName + " Setup";
        TargetPathTextBox.Text = product.SuggestedInstallDirectory;
        WelcomeText.Text = uninstall
            ? $"{product.ProductName} wird von diesem Computer entfernt. Desktop-Verknüpfung und Installationsordner werden gelöscht."
            : $"{product.ProductName} wird auf diesem Computer eingerichtet. Danach starten Sie das Programm über die Desktop-Verknüpfung – nicht aus dem Kopierordner.";
        ShowStep(0);
        if (uninstall)
        {
            NextButton.Content = "Deinstallieren";
            InstallButton.Content = "Deinstallieren";
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        WelcomePanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = step == 1 && !_uninstall ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        FinishPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = step == 1 && !_uninstall ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = step < 2 ? Visibility.Visible : Visibility.Collapsed;
        StepTitleTextBlock.Text = step switch
        {
            0 => "Schritt 1 – Willkommen",
            1 => _uninstall ? "Deinstallation" : "Schritt 2 – Installationsordner",
            2 => _uninstall ? "Wird entfernt…" : "Installation läuft…",
            _ => "Fertig"
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Installationsordner für " + _product.ProductName,
            ShowNewFolderButton = true,
            SelectedPath = TargetPathTextBox.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TargetPathTextBox.Text = dialog.SelectedPath;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstall)
            _ = RunUninstallAsync();
        else
            ShowStep(1);
    }

    private void Install_Click(object sender, RoutedEventArgs e) => _ = RunInstallAsync();

    private async Task RunInstallAsync()
    {
        var target = (TargetPathTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            System.Windows.MessageBox.Show(this, "Bitte einen Installationsordner wählen.", Title);
            return;
        }

        ShowStep(2);
        var progress = new Progress<string>(text => ProgressStatusTextBlock.Text = text);
        try
        {
            await Task.Run(() => InstallationService.Install(_product, LaunchMode.AppDirectory, target, progress));
            var exe = Path.Combine(target, _product.ExeFileName);
            DesktopShortcutService.TryCreate(_product, exe, out var shortcutMsg);
            FinishTitle.Text = "Installation abgeschlossen";
            FinishMessageTextBlock.Text = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), _product.ShortcutFileName))
                                          || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), _product.ShortcutFileName))
                ? _product.ProductName + " ist eingerichtet.\nDesktop-Verknüpfung: " + _product.ShortcutFileName
                : _product.ProductName + " ist eingerichtet, aber die Desktop-Verknüpfung fehlt:\n" + shortcutMsg;
            ShowStep(3);
            if (LaunchCheckBox.IsChecked == true && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            FinishTitle.Text = "Installation fehlgeschlagen";
            FinishMessageTextBlock.Text = ex.Message;
            ShowStep(3);
        }
    }

    private async Task RunUninstallAsync()
    {
        ShowStep(2);
        ProgressStatusTextBlock.Text = "Wird entfernt…";
        try
        {
            var dir = LaunchMode.AppDirectory;
            await Task.Run(() => InstallationService.Uninstall(_product, dir));
            FinishTitle.Text = "Deinstallation abgeschlossen";
            FinishMessageTextBlock.Text = _product.ProductName + " wurde entfernt.";
        }
        catch (Exception ex)
        {
            FinishTitle.Text = "Deinstallation fehlgeschlagen";
            FinishMessageTextBlock.Text = ex.Message;
        }
        ShowStep(3);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Finish_Click(object sender, RoutedEventArgs e) => Close();
}
