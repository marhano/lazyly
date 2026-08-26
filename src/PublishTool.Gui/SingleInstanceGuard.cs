using System.Threading;
using System.Windows;

namespace PublishTool.Gui;

/// <summary>
/// Ensures only one PublishTool.Gui process runs per Windows session -- launching it again (a
/// second desktop shortcut click, a stray taskbar-pinned launch, etc.) brings the already-running
/// window to the foreground instead of spawning a duplicate instance with its own tray icon,
/// output log, and in-flight state.
/// </summary>
internal static class SingleInstanceGuard
{
    // No "Global\" prefix -- these are scoped to the current session on purpose, so each Windows
    // user account (e.g. over separate Remote Desktop sessions) still gets their own instance;
    // only a second launch by the *same* signed-in user is what this guards against.
    private const string MutexName = "PublishTool.Gui.SingleInstance";
    private const string ActivateEventName = "PublishTool.Gui.ActivateExisting";

    // Deliberately never disposed -- both need to stay alive for the whole process lifetime so a
    // second launch keeps seeing them as held/existing; the OS cleans them up when this process exits.
    private static Mutex? _mutex;

    /// <summary>Call once, before any UI/window is created. Returns true if this is the only
    /// instance and startup should continue normally. Returns false if another instance is already
    /// running -- it's already been signaled to come to the foreground, and this process should
    /// exit immediately without doing anything else.</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        try
        {
            using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            existingEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Narrow startup race -- the running instance hasn't created its event yet. Nothing to
            // signal; harmless, it just won't come to the foreground this one time.
        }

        return false;
    }

    /// <summary>Starts a background listener that brings <paramref name="window"/> to the
    /// foreground every time a second launch calls <see cref="TryAcquire"/> and finds one already
    /// running. Call once, right after the main window is shown.</summary>
    public static void ListenForActivation(Window window)
    {
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                activateEvent.WaitOne();
                window.Dispatcher.Invoke(() =>
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            }
        })
        {
            IsBackground = true,
            Name = "PublishTool-SingleInstanceListener",
        };
        thread.Start();
    }
}
