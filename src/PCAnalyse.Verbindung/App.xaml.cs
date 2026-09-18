using System.Windows;
using PCAnalyse.Core;
using PCAnalyse.Setup;

namespace PCAnalyse.Verbindung;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (UpdateUi.TryHandleApplyUpdate(UpdateChannel.Verbindung, e.Args))
            return;

        if (LaunchMode.IsInstaller())
        {
            MainWindow = new InstallWizardWindow(ProductDefinition.Verbindung);
            MainWindow.Show();
            return;
        }

        if (LaunchMode.IsUninstaller())
        {
            MainWindow = new InstallWizardWindow(ProductDefinition.Verbindung, uninstall: true);
            MainWindow.Show();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
