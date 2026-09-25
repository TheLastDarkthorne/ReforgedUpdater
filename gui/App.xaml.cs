using System.Windows;
using System.Windows.Threading;

namespace ReforgedUpdater.Gui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // A bug in a click handler should not take a multi-gigabyte download down with it.
            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            CuteDialog.Show(Current?.MainWindow, "Oops, something went wrong",
                e.Exception.GetType().Name + ": " + e.Exception.Message, "OK", null, MascotMood.Oops);
        }
    }
}
