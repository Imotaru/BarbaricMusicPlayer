using System.Windows;
using System.Windows.Threading;

namespace Barbaric.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    // A music player should keep playing through a UI hiccup rather than crash.
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Barbaric Music Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
