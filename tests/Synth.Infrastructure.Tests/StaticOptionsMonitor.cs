using Microsoft.Extensions.Options;

namespace Synth.Infrastructure.Tests;

// Minimal IOptionsMonitor over a fixed value, so GitRepoService can be exercised without a DI
// container. Tokens/workspace root are set once per test — no live reload is needed here.
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
