using PublishTool.Core;

namespace PublishTool.Hosting;

/// <summary>
/// Streams <see cref="IOutputSink"/> calls line-by-line straight to the client over the live HTTP
/// response body (see <c>POST /api/tests/run</c> in <c>Program.cs</c>) -- the server-side counterpart
/// to how <c>GuiOutputSink</c> streams a local process's output into the GUI's own output panel.
/// <see cref="TextWriter.WriteLine(string)"/> is synchronous, matching <see cref="IOutputSink"/>'s own
/// synchronous contract exactly, so no async plumbing is needed here at all; the writer passed in is
/// expected to auto-flush (or be flushed by the caller) so each line actually reaches the client as
/// it's written rather than sitting in a buffer until the whole run finishes.
/// </summary>
internal sealed class HttpStreamOutputSink : IOutputSink
{
    private readonly TextWriter _writer;

    public HttpStreamOutputSink(TextWriter writer)
    {
        _writer = writer;
    }

    public void Info(string message) => WriteLine(message);

    public void Warn(string message) => WriteLine($"WARN: {message}");

    public void Error(string message) => WriteLine($"ERROR: {message}");

    public void Stage(string message) => WriteLine(message);

    // No OS-level notification exists for a headless server process -- the client-side GUI already
    // shows its own notification once the streamed run completes and it re-fetches the result.
    public void Notify(string title, string message, string? filePath = null)
    {
    }

    private void WriteLine(string message)
    {
        try
        {
            _writer.WriteLine(message);
        }
        catch (IOException)
        {
            // Client disconnected mid-stream (closed the GUI, network drop) -- nothing more to
            // write to, and the run itself keeps going server-side regardless.
        }
    }
}
