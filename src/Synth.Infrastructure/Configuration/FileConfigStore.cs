using Synth.Domain.Configuration;
namespace Synth.Infrastructure.Configuration;

// File-backed IConfigStore: a single JSON document under a local path
// (default ~/.synth/config.json). This is the default when no Mongo connection
// is configured — i.e. local dev without Docker running. A FileSystemWatcher
// provides live-reload; watching is best-effort and never blocks startup.
public sealed class FileConfigStore : IConfigStore, IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;

    public FileConfigStore(string? path = null)
    {
        _path = path ?? DefaultPath();

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            _watcher = TryCreateWatcher(directory, Path.GetFileName(_path));
        }
    }

    public event Action? Changed;

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;

        return await File.ReadAllTextAsync(_path, cancellationToken);
    }

    public async Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(_path, json, cancellationToken);
        Changed?.Invoke();
    }

    private FileSystemWatcher? TryCreateWatcher(string directory, string fileName)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, _) => Changed?.Invoke();
            watcher.Created += (_, _) => Changed?.Invoke();
            watcher.Renamed += (_, _) => Changed?.Invoke();
            return watcher;
        }
        catch
        {
            // Live-reload is a convenience; if the platform can't watch, the store
            // still loads and saves. Fall back to no watcher rather than failing.
            return null;
        }
    }

    private static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".synth", "config.json");
    }

    public void Dispose() => _watcher?.Dispose();
}
