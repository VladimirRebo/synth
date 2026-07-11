namespace Synth.Domain.Configuration;

// The single source for Synth's mutable configuration document (layer 2 in the
// config-layering scheme: appsettings -> IConfigStore -> environment variables).
//
// The document is an opaque JSON string; the store only persists and retrieves it.
// Flattening into IConfiguration keys is the ConfigStoreConfigurationProvider's job.
// Adapted from Sonar's config-layering shape, simplified for a personal project.
public interface IConfigStore
{
    // Returns the stored JSON document, or null when nothing has been saved yet
    // (or the backing store is unreachable — callers treat that as "empty").
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    // Persists the whole JSON document, replacing any previous value.
    Task SaveAsync(string json, CancellationToken cancellationToken = default);

    // Raised when the stored document changes, so IConfiguration can live-reload
    // and IOptionsMonitor<T> subscribers pick up updates without a restart.
    event Action? Changed;
}
