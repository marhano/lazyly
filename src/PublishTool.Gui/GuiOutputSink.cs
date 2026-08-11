using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using PublishTool.Core;

namespace PublishTool.Gui;

internal sealed class GuiOutputSink : IOutputSink
{
    private readonly TextBox _logBox;
    private readonly TextBlock _statusTextBlock;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private int _flushScheduled;
    private string? _pendingRevealPath;

    public GuiOutputSink(TextBox logBox, TextBlock statusTextBlock, System.Windows.Forms.NotifyIcon notifyIcon)
    {
        _logBox = logBox;
        _statusTextBlock = statusTextBlock;
        _notifyIcon = notifyIcon;
        _notifyIcon.BalloonTipClicked += (_, _) => RevealPendingPath();
    }

    public void Info(string message) => Append(message);

    public void Warn(string message) => Append($"WARN: {message}");

    public void Error(string message) => Append($"ERROR: {message}");

    public void Stage(string message)
    {
        Append(message);
        _statusTextBlock.Dispatcher.Invoke(() => _statusTextBlock.Text = message);
    }

    public void Notify(string title, string message, string? filePath = null)
    {
        _pendingRevealPath = filePath;

        _logBox.Dispatcher.Invoke(() =>
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = filePath is null ? message : $"{message}\nClick to open build folder";
            _notifyIcon.ShowBalloonTip(8000);
        });
    }

    private void RevealPendingPath()
    {
        if (_pendingRevealPath is null || !File.Exists(_pendingRevealPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_pendingRevealPath}\"",
            UseShellExecute = true,
        });
    }

    // MSBuild can emit hundreds of output lines in a burst (CoreCompile warnings, etc.).
    // Dispatching one synchronous UI call per line saturates the UI thread's message
    // queue and reads as a freeze. Instead, queue lines and coalesce them into a single
    // low-priority, non-blocking flush, so a burst of 500 lines costs one UI update, not 500.
    private void Append(string message)
    {
        _pendingLines.Enqueue(message);

        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        _logBox.Dispatcher.BeginInvoke(new Action(FlushPendingLines), DispatcherPriority.Background);
    }

    private void FlushPendingLines()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        var batch = new StringBuilder();
        while (_pendingLines.TryDequeue(out var line))
        {
            batch.Append(line).Append(Environment.NewLine);
        }

        if (batch.Length == 0)
        {
            return;
        }

        _logBox.AppendText(batch.ToString());
        _logBox.ScrollToEnd();
    }
}
