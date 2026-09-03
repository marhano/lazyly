using System.ComponentModel;

namespace PublishTool.Gui;

/// <summary>Display row for the Settings tab's Dev Server Relays grid -- wraps a
/// <c>RemoteHostingRelay</c> with a computed <see cref="IsActive"/> flag (whether its URL matches
/// whatever's currently saved as the active Remote Build Hosting URL) so the grid can show which
/// one, if any, is currently in use.</summary>
public sealed class RemoteHostingRelayRow : INotifyPropertyChanged
{
    private string _connectionStatus = "Checking...";

    public required string Name { get; set; }

    public required string Url { get; set; }

    public bool IsActive { get; set; }

    /// <summary>"Checking...", "Online", "Offline", or an error message. Set asynchronously after
    /// the row is first displayed -- see <see cref="MainWindow.LoadRemoteHostingRelaysIntoForm"/>,
    /// which pings every relay in the background right after building the list, so the grid appears
    /// immediately instead of waiting on the network. Needs <see cref="INotifyPropertyChanged"/>
    /// since the row is already bound and on screen by the time the ping resolves.</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set
        {
            if (_connectionStatus == value)
            {
                return;
            }

            _connectionStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
