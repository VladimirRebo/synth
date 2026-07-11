using Synth.Api.Configuration;

namespace Synth.Api.Tests;

// FileConfigStore is the default store for local dev without Docker. These tests
// use a throwaway temp path so they never touch the real ~/.synth/config.json and
// never require Mongo/Docker.
public class FileConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public FileConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "synth-tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_tempDir, "config.json");
    }

    [Fact]
    public async Task Save_then_load_round_trips_the_document()
    {
        using var store = new FileConfigStore(_path);
        const string json = """{"Section":{"Key":"value"}}""";

        await store.SaveAsync(json);
        var loaded = await store.LoadAsync();

        Assert.Equal(json, loaded);
    }

    [Fact]
    public async Task Load_returns_null_when_nothing_saved_yet()
    {
        using var store = new FileConfigStore(_path);

        var loaded = await store.LoadAsync();

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_raises_changed()
    {
        using var store = new FileConfigStore(_path);
        var raised = false;
        store.Changed += () => raised = true;

        await store.SaveAsync("{}");

        Assert.True(raised);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
