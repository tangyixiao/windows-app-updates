using System.Windows;

namespace WindowsAppUpdates;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        instance = new Mutex(true, $@"Local\WindowsAppUpdates-{identity.User?.Value}", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("应用更新已打开，请从任务栏切换到已有窗口。", "应用更新");
            Shutdown();
            return;
        }

        MainWindow = new MainWindow(new WinGet(), new HistoryStore());
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
