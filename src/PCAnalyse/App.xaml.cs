using System.Windows;
using PCAnalyse.Core;
using PCAnalyse.Setup;

namespace PCAnalyse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (UpdateUi.TryHandleApplyUpdate(UpdateChannel.Main, e.Args))
            return;

        if (LaunchMode.IsInstaller())
        {
            MainWindow = new InstallWizardWindow(ProductDefinition.MainApp);
            MainWindow.Show();
            return;
        }

        if (LaunchMode.IsUninstaller())
        {
            MainWindow = new InstallWizardWindow(ProductDefinition.MainApp, uninstall: true);
            MainWindow.Show();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
