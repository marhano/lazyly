using System.Configuration;
using System.Data;
using System.IO;
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

        // No StartupUri (Program.cs is the real entry point now, ahead of VelopackApp's own
        // startup hook), so the main window has to be shown explicitly.
        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // e.Exception is frequently just a framework wrapper (e.g. TargetInvocationException from
        // a constructor call) whose own .Message says nothing about what actually went wrong --
        // the real cause is further down the InnerException chain, so walk to the bottom of it.
        var root = e.Exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        var logPath = TryWriteCrashLog(e.Exception);

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{root.Message}\n\n{root.StackTrace}\n\n" +
            (logPath is not null ? $"Full details were written to:\n{logPath}\n\n" : string.Empty) +
            "PublishTool will try to keep running, but if this keeps happening, please note what you were doing when it occurred.",
            "PublishTool",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    /// <summary>Best-effort -- a crash log is a diagnostic nicety, not something that should itself
    /// crash the crash handler if e.g. the disk is full or the folder is locked down.</summary>
    private static string? TryWriteCrashLog(Exception exception)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PublishTool");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
