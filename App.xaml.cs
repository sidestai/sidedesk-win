using System.Windows;

namespace SideDesk
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Exit += App_Exit;
            SessionEnding += App_SessionEnding;
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {
            CleanupAppBar();
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            CleanupAppBar();
        }

        private void CleanupAppBar()
        {
            if (MainWindow is AppBarWindow appBar)
            {
                appBar.UnregisterAppBar();
            }
        }
    }
}
