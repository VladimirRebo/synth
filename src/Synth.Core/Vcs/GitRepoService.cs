using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Synth.Core.Vcs;

/// <summary>
/// Ensures a remote GitHub/GitLab repository is available as a local checkout: clones it the first
/// time, fetches + hard-resets it on subsequent calls. Shells out to the <c>git</c> CLI (no NuGet
/// git dependency). A trimmed-down mirror of Sonar's <c>GitRepoService</c> — no webhooks, no
/// Bitbucket <c>/scm/</c> special-casing, no CI auth plumbing. Auth tokens (when configured) are
/// passed to git via an in-memory <c>http.extraHeader</c>, so they never land on disk or in the
/// stored remote URL.
/// </summary>
public sealed class GitRepoService
{
    private readonly IOptionsMonitor<VcsOptions> _options;

    // One lock per checkout path (keyed by slug, stable across calls since it's a pure function of
    // the repo URL) so two concurrent EnsureRepoAsync calls for the same repo can't interleave
    // clone/fetch/reset/delete against the same directory. Entries are intentionally never removed —
    // the set of distinct repos indexed in a process lifetime is small and bounded by user action.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> CheckoutLocks = new();

    public GitRepoService(IOptionsMonitor<VcsOptions> options) => _options = options;

    /// <summary>
    /// Returns the local checkout path for <paramref name="repoUrl"/>, cloning or refreshing as needed.
    /// </summary>
    /// <param name="repoUrl">HTTPS (or <c>file://</c>) git remote URL.</param>
    /// <param name="branch">
    /// Branch to check out; when null/empty the repository's default branch (<c>origin/HEAD</c>) is used.
    /// </param>
    public async Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        var info = RepoUrlInfo.Parse(repoUrl);
        var options = _options.CurrentValue;
        var root = ResolveWorkspaceRoot(options.WorkspaceRoot);
        var checkout = Path.Combine(root, info.Slug);
        var auth = AuthArgs(info.Provider, TokenFor(info.Provider, options));

        var checkoutLock = CheckoutLocks.GetOrAdd(info.Slug, _ => new SemaphoreSlim(1, 1));
        await checkoutLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsGitCheckout(checkout))
            {
                // Existing checkout: fetch the latest refs and move the working tree to the target,
                // discarding any local drift. Mirrors Sonar's re-clone/refresh, minus the special-casing.
                await RunGitAsync(checkout, [.. auth, "fetch", "--prune", "origin"], cancellationToken).ConfigureAwait(false);
                var target = string.IsNullOrWhiteSpace(branch) ? "origin/HEAD" : $"origin/{branch}";
                await RunGitAsync(checkout, [.. auth, "reset", "--hard", target], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(root);
                // A leftover non-git directory (interrupted clone) would make `git clone` fail into a
                // non-empty target, so clear it and start clean.
                if (Directory.Exists(checkout))
                    Directory.Delete(checkout, recursive: true);

                var clone = new List<string>(auth) { "clone" };
                if (!string.IsNullOrWhiteSpace(branch))
                {
                    clone.Add("--branch");
                    clone.Add(branch);
                }

                clone.Add(repoUrl);
                clone.Add(checkout);
                await RunGitAsync(root, clone, cancellationToken).ConfigureAwait(false);
            }

            return checkout;
        }
        finally
        {
            checkoutLock.Release();
        }
    }

    /// <summary>
    /// Resolves the on-disk checkout directory for a repoUrl-indexed collection without cloning or
    /// fetching: it is <c>{WorkspaceRoot}/{slug}</c>, the same location <see cref="EnsureRepoAsync"/>
    /// checks the repository out into (and <paramref name="slug"/> equals the collection name — see
    /// <c>IndexingEndpoints.StartIndexing</c>). Reuses the same <see cref="ResolveWorkspaceRoot"/>
    /// default-path/env-expansion logic as the clone path, so the two never drift. Used by the
    /// <c>get_file</c> MCP tool to read a file out of an already-indexed remote checkout.
    /// </summary>
    public string ResolveCheckoutPath(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return Path.Combine(ResolveWorkspaceRoot(), slug);
    }

    /// <summary>
    /// Resolves the configured workspace root (the directory that holds one checkout subdirectory per
    /// repository slug), applying the same default-path/env-expansion logic <see cref="EnsureRepoAsync"/>
    /// and <see cref="ResolveCheckoutPath"/> use, so the three never drift. Used by the startup orphan
    /// sweep (SYNTH-45) to enumerate on-disk checkouts and drop the ones with no registry entry.
    /// </summary>
    public string ResolveWorkspaceRoot() => ResolveWorkspaceRoot(_options.CurrentValue.WorkspaceRoot);

    // A directory counts as a usable checkout only if it carries a .git entry (dir for a normal
    // clone, file for a worktree/submodule). Otherwise we (re)clone.
    private static bool IsGitCheckout(string checkout)
    {
        if (!Directory.Exists(checkout))
            return false;

        var gitPath = Path.Combine(checkout, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private static string ResolveWorkspaceRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".synth", "workspaces");
    }

    private static string? TokenFor(GitProvider provider, VcsOptions options) => provider switch
    {
        GitProvider.GitHub => options.GitHub?.Token,
        GitProvider.GitLab => options.GitLab?.Token,
        _ => null,
    };

    // Global `git -c http.extraHeader=...` args placed before the subcommand. Kept out of the remote
    // URL and off disk (no credential helper). Empty for public repos / unknown providers.
    private static IReadOnlyList<string> AuthArgs(GitProvider provider, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return [];

        var header = provider switch
        {
            GitProvider.GitHub => $"Authorization: Bearer {token}",
            GitProvider.GitLab => $"PRIVATE-TOKEN: {token}",
            _ => null,
        };

        return header is null ? [] : ["-c", $"http.extraHeader={header}"];
    }

    private static async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitCommandException("git", -1, string.Empty, $"failed to start git: {ex.Message}");
        }

        // Disposing the Process wrapper on cancellation does not kill the OS process it wraps — without
        // this registration a cancelled git invocation would keep running detached, still writing into
        // the checkout directory. entireProcessTree covers git's own child processes (e.g. transport
        // helpers) too.
        await using var killOnCancel = cancellationToken.Register(static state =>
        {
            var p = (Process)state!;
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill — nothing to do.
            }
        }, process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new GitCommandException(Redact(arguments), process.ExitCode, stdout, stderr);

        return stdout;
    }

    // Rebuilds the command line for error messages with any auth header value masked, so a failing
    // git call can be logged without leaking the token that lives in `-c http.extraHeader=...`.
    private static string Redact(IReadOnlyList<string> arguments)
    {
        var safe = arguments
            .Select(a => a.StartsWith("http.extraHeader=", StringComparison.Ordinal) ? "http.extraHeader=***" : a);
        return string.Join(' ', safe);
    }
}

/// <summary>Thrown when a <c>git</c> invocation exits non-zero. Never carries the auth token — that
/// lives only in the process arguments, not in the captured message.</summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException(string command, int exitCode, string standardOutput, string standardError)
        : base($"git {command} exited with code {exitCode}: {standardError.Trim()}")
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }
}
