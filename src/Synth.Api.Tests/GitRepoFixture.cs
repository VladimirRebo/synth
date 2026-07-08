using System.Diagnostics;

namespace Synth.Api.Tests;

// A throwaway local git repository reachable over a file:// URL — the same fixture style as
// SYNTH-18's GitRepoServiceTests, so the RepoUrl indexing path can be exercised end to end with
// no network access to github.com/gitlab.com. Builds a bare "origin" plus an authoring working
// copy seeded with a single C# file, then exposes the origin's file:// URL.
internal sealed class GitRepoFixture : IDisposable
{
    private readonly string _root;

    private GitRepoFixture(string root, string url)
    {
        _root = root;
        Url = url;
    }

    // file:///... URL of the bare origin repo, suitable as POST /index { repoUrl: ... }.
    public string Url { get; }

    public static GitRepoFixture CreateWithCSharpFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "synth-index-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var originPath = Path.Combine(root, "origin.git");
        var originUrl = new Uri(originPath).AbsoluteUri;
        var authoring = Path.Combine(root, "authoring");

        Git(root, "init", "--bare", "-b", "main", originPath);
        Git(root, "clone", originUrl, authoring);

        File.WriteAllText(Path.Combine(authoring, "Sample.cs"), """
            namespace Sample;

            public class Greeter
            {
                public string Greet(string name) => $"Hello, {name}!";
            }
            """);
        Git(authoring, "add", "Sample.cs");
        Git(authoring, "commit", "-m", "add Sample.cs");
        Git(authoring, "push", "-u", "origin", "main");

        return new GitRepoFixture(root, originUrl);
    }

    // Runs git with a throwaway identity so no global git config is needed.
    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var config in new[]
                 {
                     "-c", "user.email=test@synth.local",
                     "-c", "user.name=Synth Test",
                     "-c", "commit.gpgsign=false",
                     "-c", "init.defaultBranch=main",
                 })
        {
            startInfo.ArgumentList.Add(config);
        }

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp fixture.
        }
    }
}
