using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synth.Core.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Vcs;

/// <summary>
/// One-shot startup sweep (SYNTH-45) that garbage-collects orphaned git checkouts under the workspace
/// root: subdirectories left behind by collections deleted before checkout cleanup existed, or removed
/// out-of-band. It lists the workspace-root subdirectories, compares their names against the known
/// collections from <see cref="IRepositoryRegistry"/>, and deletes any subdirectory that matches no
/// entry. Runs once at startup and returns — no recurring loop. There is no age-based eviction: a
/// checkout is removed only when nothing in the registry claims it, never guessed stale by time.
/// Registered like <c>LogEntryStoreWriter</c> (SYNTH-28) via <c>AddHostedService</c> in Program.cs.
/// </summary>
public sealed class OrphanCheckoutSweeper : BackgroundService
{
    private readonly IRepositoryRegistry _registry;
    private readonly GitRepoService _gitRepoService;
    private readonly ILogger<OrphanCheckoutSweeper> _logger;

    public OrphanCheckoutSweeper(
        IRepositoryRegistry registry,
        GitRepoService gitRepoService,
        ILogger<OrphanCheckoutSweeper> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gitRepoService = gitRepoService ?? throw new ArgumentNullException(nameof(gitRepoService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown before the sweep finished — nothing more to do.
        }
        catch (Exception ex)
        {
            // A failed sweep must never take the host down: orphaned checkouts are a disk-space
            // nuisance, not a correctness problem, and the next startup retries the sweep.
            _logger.LogWarning(ex, "Orphaned-checkout sweep failed.");
        }
    }

    /// <summary>
    /// Runs the sweep once against the current registry and workspace root. Public so it can be driven
    /// synchronously in tests without hosting a full app; <see cref="ExecuteAsync"/> just calls it once.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var root = _gitRepoService.ResolveWorkspaceRoot();

        // Fresh install: nothing has been cloned yet, so there is nothing to sweep. Guarding here also
        // keeps Directory.GetDirectories from throwing on a not-yet-created workspace root.
        if (!Directory.Exists(root))
            return;

        var known = (await _registry.ListAsync(cancellationToken))
            .Select(e => e.Collection)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var directory in Directory.GetDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (known.Contains(name))
                continue;

            RepositoryEndpoints.DeleteCheckout(directory);
            _logger.LogInformation(
                "Removed orphaned checkout '{Checkout}' (no matching indexed collection).", name);
        }
    }
}
