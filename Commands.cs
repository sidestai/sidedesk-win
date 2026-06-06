using System;
using System.Windows.Input;

namespace SideDesk
{
    public class ActivateCommand : ICommand
    {
        public void Execute(object? parameter)
        {
            var mainWindow = System.Windows.Application.Current.MainWindow;

            if (mainWindow == null)
            {
                return;
            }

            mainWindow.Activate();
        }

        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public event EventHandler? CanExecuteChanged;
    }
}
