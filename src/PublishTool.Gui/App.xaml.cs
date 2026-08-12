using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace PublishTool.Gui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without this, any unhandled exception on the UI thread (a bug in some rarely-hit code
        // path, a third-party control misbehaving, etc.) silently kills the whole app -- taking
        // down whatever the user had in progress (unsaved form fields, an in-flight publish) with
        // no explanation. Show what happened instead and keep running wherever that's possible.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nPublishTool will try to keep running, " +
            "but if this keeps happening, please note what you were doing when it occurred.",
            "PublishTool",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
