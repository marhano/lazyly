namespace PublishTool.Core.Services;

public sealed record GitBranchInfo(string CurrentBranch, IReadOnlyList<string> Branches);

/// <summary>
/// Thrown by <see cref="GitService.CheckoutAsync"/> specifically when checkout failed because
/// uncommitted local changes would be overwritten -- as opposed to any other failure -- so a
/// caller (the GUI) can offer to resolve it (discard/stash/commit those files) instead of just
/// surfacing a generic error.
/// </summary>
public sealed class GitCheckoutConflictException : InvalidOperationException
{
    public GitCheckoutConflictException(string branch, IReadOnlyList<string> conflictingFiles)
        : base($"Can't check out '{branch}': local changes to {conflictingFiles.Count} file(s) would be overwritten.")
    {
        Branch = branch;
        ConflictingFiles = conflictingFiles;
    }

    public string Branch { get; }

    public IReadOnlyList<string> ConflictingFiles { get; }
}

/// <summary>
/// Thin wrapper around the git CLI for switching a registered project's working tree to a
/// different branch before publishing. Uses git's own "-C &lt;dir&gt;" option to target the
/// project's repo rather than shelling out with a changed working directory, so it composes
/// fine with the rest of ProcessRunner's fire-and-forget usage elsewhere in this codebase.
/// </summary>
public sealed class GitService
{
    private readonly IOutputSink _output;

    public GitService(IOutputSink output)
    {
        _output = output;
    }

    /// <summary>True if the directory containing <paramref name="csprojPath"/> is inside a git
    /// working tree. Projects that aren't in git just don't get branch-switching UI -- that's
    /// not an error condition.</summary>
    public async Task<bool> IsGitRepositoryAsync(string csprojPath, CancellationToken ct = default)
    {
        var (exitCode, _) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{RepoDir(csprojPath)}\" rev-parse --is-inside-work-tree", ct);
        return exitCode == 0;
    }

    /// <summary>The currently checked-out branch name, or null if this isn't a git repository.</summary>
    public async Task<string?> GetCurrentBranchAsync(string csprojPath, CancellationToken ct = default)
    {
        var (exitCode, output) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{RepoDir(csprojPath)}\" rev-parse --abbrev-ref HEAD", ct);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Tracked files with uncommitted changes (modified, staged, or deleted) -- deliberately
    /// excludes untracked files, since those never block or get carried along by a checkout the
    /// way tracked changes do. Paths are repo-root-relative, same convention as
    /// <see cref="GitCheckoutConflictException.ConflictingFiles"/>, so both can feed the same
    /// Discard/Stash/Commit methods. Returns an empty list (not an error) if this isn't a repo.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUncommittedChangesAsync(string csprojPath, CancellationToken ct = default)
    {
        var (exitCode, repoRootOrError) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{RepoDir(csprojPath)}\" rev-parse --show-toplevel", ct);
        if (exitCode != 0)
        {
            return Array.Empty<string>();
        }

        var repoRoot = repoRootOrError.Trim().Replace('/', Path.DirectorySeparatorChar);
        var (statusExit, statusOutput) = await ProcessRunner.RunCapturedAsync("git", $"-C \"{repoRoot}\" status --porcelain", ct);
        if (statusExit != 0)
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        foreach (var line in statusOutput.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4 || line.StartsWith("??", StringComparison.Ordinal))
            {
                continue; // "??" is untracked -- not relevant to a checkout carrying changes over
            }

            files.Add(line[3..].Trim());
        }

        return files;
    }

    /// <summary>Lists local and remote branches (deduplicated by name), with the currently
    /// checked-out branch first. Returns null if this isn't a git repository. Does a best-effort
    /// "git fetch --prune" first so newly pushed branches show up -- failures there (e.g. no
    /// network) are swallowed rather than failing the whole listing.</summary>
    public async Task<GitBranchInfo?> ListBranchesAsync(string csprojPath, CancellationToken ct = default)
    {
        var dir = RepoDir(csprojPath);

        var (headExit, headOutput) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{dir}\" rev-parse --abbrev-ref HEAD", ct);
        if (headExit != 0)
        {
            return null;
        }

        var currentBranch = headOutput.Trim();

        await ProcessRunner.RunSilentAsync("git", $"-C \"{dir}\" fetch --prune", ct);

        var branches = new List<string>();

        // Local and remote refs are queried separately (not "refs/heads refs/remotes" together)
        // because they need different name-stripping rules: a local branch's refname:short IS its
        // full name, slashes and all (e.g. "release/1.4", "feature/foo" are common conventions),
        // while a remote ref is prefixed with the remote's own name ("origin/release/1.4") which
        // is what actually needs stripping. Treating any slash as a remote prefix (as an earlier
        // version of this method did) mangled local branches like "release/1.4" into "1.4".
        var (localExit, localOutput) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{dir}\" for-each-ref --format=\"%(refname:short)\" refs/heads", ct);
        if (localExit == 0)
        {
            AddBranchNames(branches, localOutput, stripRemotePrefix: false);
        }

        var (remoteExit, remoteOutput) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{dir}\" for-each-ref --format=\"%(refname:short)\" refs/remotes", ct);
        if (remoteExit == 0)
        {
            AddBranchNames(branches, remoteOutput, stripRemotePrefix: true);
        }

        if (!branches.Contains(currentBranch, StringComparer.OrdinalIgnoreCase))
        {
            branches.Insert(0, currentBranch);
        }

        branches.Sort(StringComparer.OrdinalIgnoreCase);
        return new GitBranchInfo(currentBranch, branches);
    }

    private static void AddBranchNames(List<string> branches, string refOutput, bool stripRemotePrefix)
    {
        foreach (var raw in refOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0 || name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue; // "origin/HEAD" is a pointer, not a real branch
            }

            // Remote names (the part before the first slash, e.g. "origin") don't themselves
            // contain slashes, so stripping up to the first slash is safe here -- unlike for
            // local branch names, where the whole string (slashes included) is the branch name.
            var simpleName = stripRemotePrefix && name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;
            if (!branches.Contains(simpleName, StringComparer.OrdinalIgnoreCase))
            {
                branches.Add(simpleName);
            }
        }
    }

    /// <summary>Checks out <paramref name="branch"/> in the project's repo -- creating a local
    /// tracking branch from origin/<paramref name="branch"/> if it only exists remotely so far.
    /// No-ops (just logs) if already on that branch. Throws <see cref="GitCheckoutConflictException"/>
    /// (not the base InvalidOperationException) specifically when uncommitted local changes are
    /// what's blocking it, so a caller can offer to resolve those files instead of just failing.</summary>
    public async Task CheckoutAsync(string csprojPath, string branch, CancellationToken ct = default)
    {
        var dir = RepoDir(csprojPath);

        var currentBranch = await GetCurrentBranchAsync(csprojPath, ct);
        if (string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase))
        {
            _output.Info($"Already on branch '{branch}'.");
            return;
        }

        var (localExists, _) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{dir}\" show-ref --verify --quiet refs/heads/{branch}", ct);

        var checkoutArgs = localExists == 0
            ? $"-C \"{dir}\" checkout \"{branch}\""
            : $"-C \"{dir}\" checkout -b \"{branch}\" --track \"origin/{branch}\"";

        _output.Stage($"Checking out git branch '{branch}'...");

        // Captured (not streamed) so a conflict failure can be parsed for the file list -- still
        // logged below afterward, so nothing about the visible output changes for the normal case.
        var (exitCode, checkoutOutput) = await ProcessRunner.RunCapturedAsync("git", checkoutArgs, ct);
        foreach (var line in checkoutOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            _output.Info(line.TrimEnd('\r'));
        }

        if (exitCode != 0)
        {
            var conflictingFiles = ParseCheckoutConflictFiles(checkoutOutput);
            if (conflictingFiles.Count > 0)
            {
                throw new GitCheckoutConflictException(branch, conflictingFiles);
            }

            throw new InvalidOperationException(
                $"Failed to check out branch '{branch}' (git exited with code {exitCode}). " +
                "If the working tree has uncommitted changes that conflict with this branch, commit, stash, or discard them first.");
        }
    }

    /// <summary>Discards local changes to exactly the given files (git checkout -- &lt;files&gt;),
    /// resetting them to HEAD's version. Does not touch any other files in the repo.</summary>
    public async Task DiscardChangesAsync(string csprojPath, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var repoRoot = await GetRepoRootAsync(csprojPath, ct);
        var fileArgs = QuoteFiles(files);

        _output.Stage($"Discarding local changes to {files.Count} file(s)...");
        var exitCode = await ProcessRunner.RunAsync("git", $"-C \"{repoRoot}\" checkout -- {fileArgs}", _output, treatStderrAsError: false, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to discard changes (git exited with code {exitCode}).");
        }
    }

    /// <summary>Stashes local changes to exactly the given files (git stash push -- &lt;files&gt;).
    /// Does not touch any other in-progress changes elsewhere in the repo.</summary>
    public async Task StashChangesAsync(string csprojPath, IReadOnlyList<string> files, string message, CancellationToken ct = default)
    {
        var repoRoot = await GetRepoRootAsync(csprojPath, ct);
        var fileArgs = QuoteFiles(files);

        _output.Stage($"Stashing local changes to {files.Count} file(s)...");
        var exitCode = await ProcessRunner.RunAsync(
            "git", $"-C \"{repoRoot}\" stash push -m \"{message}\" -- {fileArgs}", _output, treatStderrAsError: false, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Failed to stash changes (git exited with code {exitCode}).");
        }
    }

    /// <summary>Commits exactly the given files (git add -- &lt;files&gt; then git commit). Does
    /// not stage or commit any other pending changes elsewhere in the repo.</summary>
    public async Task CommitChangesAsync(string csprojPath, IReadOnlyList<string> files, string message, CancellationToken ct = default)
    {
        var repoRoot = await GetRepoRootAsync(csprojPath, ct);
        var fileArgs = QuoteFiles(files);

        _output.Stage($"Committing {files.Count} file(s)...");
        var addExitCode = await ProcessRunner.RunAsync("git", $"-C \"{repoRoot}\" add -- {fileArgs}", _output, treatStderrAsError: false, ct);
        if (addExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to stage changes (git exited with code {addExitCode}).");
        }

        var commitExitCode = await ProcessRunner.RunAsync(
            "git", $"-C \"{repoRoot}\" commit -m \"{message}\"", _output, treatStderrAsError: false, ct);
        if (commitExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to commit changes (git exited with code {commitExitCode}).");
        }
    }

    /// <summary>
    /// Parses git's "local changes would be overwritten by checkout" error into the list of
    /// affected file paths (repo-root-relative, exactly as git reports them):
    /// <code>
    /// error: Your local changes to the following files would be overwritten by checkout:
    ///         path/to/file.txt
    ///         another/file.txt
    /// Please commit your changes or stash them before you switch branches.
    /// </code>
    /// Returns an empty list if the output doesn't match this specific shape -- callers treat
    /// that as "some other failure", not a conflict.
    /// </summary>
    private static List<string> ParseCheckoutConflictFiles(string gitOutput)
    {
        var files = new List<string>();
        var inFileList = false;

        foreach (var rawLine in gitOutput.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Contains("would be overwritten by checkout", StringComparison.OrdinalIgnoreCase))
            {
                inFileList = true;
                continue;
            }

            if (!inFileList)
            {
                continue;
            }

            if (rawLine.StartsWith('\t'))
            {
                files.Add(rawLine.Trim());
            }
            else if (files.Count > 0)
            {
                break; // first non-indented line after the list ends it
            }
        }

        return files;
    }

    /// <summary>
    /// File paths in git's own messages (and thus in <see cref="GitCheckoutConflictException.ConflictingFiles"/>)
    /// are relative to the repo root, not necessarily to the project's own directory -- a project
    /// can be a subdirectory of a larger repo. Path-scoped commands (checkout/stash/add --
    /// &lt;files&gt;) need to run with -C set to that same repo root, or the paths resolve wrong.
    /// </summary>
    private static async Task<string> GetRepoRootAsync(string csprojPath, CancellationToken ct)
    {
        var (exitCode, output) = await ProcessRunner.RunCapturedAsync(
            "git", $"-C \"{RepoDir(csprojPath)}\" rev-parse --show-toplevel", ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException("Couldn't determine the git repository root.");
        }

        // git always reports this with forward slashes, even on Windows.
        return output.Trim().Replace('/', Path.DirectorySeparatorChar);
    }

    private static string QuoteFiles(IReadOnlyList<string> files) => string.Join(' ', files.Select(f => $"\"{f}\""));

    private static string RepoDir(string csprojPath) => Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
}
