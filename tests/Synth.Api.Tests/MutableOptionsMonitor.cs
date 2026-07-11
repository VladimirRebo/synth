using Microsoft.Extensions.Options;

namespace Synth.Api.Tests;

// A test IOptionsMonitor<T> whose CurrentValue can be replaced at runtime, so a config change can be
// pushed through the same surface the app uses (IOptionsMonitor). Mirrors the Vcs tests'
// StaticOptionsMonitor but adds Set(...) to simulate a live Settings save. Not thread-safe by design —
// tests drive it single-threaded.
internal sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];
    private T _value;

    public MutableOptionsMonitor(T value) => _value = value;

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public void Set(T value)
    {
        _value = value;
        foreach (var listener in _listeners.ToArray())
            listener(value, null);
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(_listeners, listener);
    }

    private sealed class Subscription(List<Action<T, string?>> listeners, Action<T, string?> listener) : IDisposable
    {
        public void Dispose() => listeners.Remove(listener);
    }
}
