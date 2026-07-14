using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synth.Application.Configuration;
using Synth.Application.Cqrs;
using Synth.Domain.Vcs;
using static Synth.Application.Configuration.JsonElementHelpers;

namespace Synth.Application.Vcs;

/// <summary>
/// Handles <see cref="UpdateVcsSettingsCommand"/>: the partial write over the <c>Vcs</c> config section
/// (<see cref="VcsOptions"/>). Writes are partial (an absent field is left unchanged, an explicit empty
/// string clears a token) and persist through <see cref="IConfigSectionUpdater"/> so the change is
/// picked up live by <c>IOptionsMonitor&lt;VcsOptions&gt;</c>.
/// <para>
/// A newly-set, non-empty GitHub/GitLab token is <b>probed-before-persist</b> (SYNTH-37): a quick
/// authenticated call against the provider's public API must succeed before the token is saved. A
/// non-2xx response or a network failure yields a <see cref="UpdateVcsSettingsResult.ValidationError"/>
/// (mapped to 400 at the controller) and nothing is persisted — the same contract as the embedding
/// probe, so an invalid/expired token is caught here instead of surfacing later deep inside a background
/// indexing job. Self-hosted GitLab is out of scope (no configurable host to probe), so GitLab is
/// validated against <c>gitlab.com</c> only.
/// </para>
/// <para>
/// SYNTH-68 lifted this out of <c>VcsSettingsEndpoints</c>'s PUT handler unchanged so it lives behind
/// the CQRS seam (issue #82), following the pattern <c>IndexRepositoryCommandHandler</c> and
/// <c>DeleteCollectionCommandHandler</c> established: the dependencies it used to take as endpoint
/// parameters are now constructor-injected, and it depends on the <see cref="IConfigSectionUpdater"/>
/// port rather than the concrete <c>ConfigSectionUpdater</c> so Application never references
/// Infrastructure.
/// </para>
/// </summary>
public sealed class UpdateVcsSettingsCommandHandler
    : ICommandHandler<UpdateVcsSettingsCommand, UpdateVcsSettingsResult>
{
    private const string WorkspaceRootKey = "WorkspaceRoot";
    private const string GitHubKey = "GitHub";
    private const string GitLabKey = "GitLab";
    private const string TokenKey = "Token";

    // Public API endpoints hit for the auth check (see VcsOptions: no configurable host, hence
    // gitlab.com only). A short timeout so an unreachable/hung provider fails the PUT quickly.
    private const string GitHubProbeUrl = "https://api.github.com/user";
    private const string GitLabProbeUrl = "https://gitlab.com/api/v4/user";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IConfigSectionUpdater _updater;
    private readonly IOptionsMonitor<VcsOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public UpdateVcsSettingsCommandHandler(
        IConfigSectionUpdater updater,
        IOptionsMonitor<VcsOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _updater = updater;
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<UpdateVcsSettingsResult> HandleAsync(
        UpdateVcsSettingsCommand command, CancellationToken cancellationToken = default)
    {
        var body = command.Body;

        if (body.ValueKind != JsonValueKind.Object)
            return UpdateVcsSettingsResult.ValidationError("Request body must be a JSON object.");

        // Probe a newly-set, non-empty token before persisting so an invalid/expired token is
        // rejected here rather than the next time GitRepoService actually uses it. Cleared or
        // omitted tokens (and workspaceRoot) need no probe — see TryGetNewToken.
        if (TryGetNewToken(body, "github", out var gitHubToken))
        {
            var error = await ProbeGitHubAsync(gitHubToken, cancellationToken);
            if (error is not null)
                return UpdateVcsSettingsResult.ValidationError(error);
        }

        if (TryGetNewToken(body, "gitlab", out var gitLabToken))
        {
            var error = await ProbeGitLabAsync(gitLabToken, cancellationToken);
            if (error is not null)
                return UpdateVcsSettingsResult.ValidationError(error);
        }

        await _updater.UpdateSectionAsync(VcsOptions.SectionName, section =>
        {
            // Present & non-null -> set; present & null -> clear; absent -> leave unchanged.
            if (TryGetPropertyIgnoreCase(body, "workspaceRoot", out var workspaceRoot))
                section[WorkspaceRootKey] = ToStringValueOrNull(workspaceRoot);

            ApplyTokenUpdate(body, "github", section, GitHubKey);
            ApplyTokenUpdate(body, "gitlab", section, GitLabKey);
        }, cancellationToken);

        // The store's Changed event has already reloaded IConfiguration synchronously on save,
        // so CurrentValue reflects the just-persisted values (this is what proves the reload path).
        return UpdateVcsSettingsResult.Ok(VcsSettingsResponse.Mask(_options.CurrentValue));
    }

    // Applies the token for one provider block: only touches the section when the block AND its
    // "token" field are present, so a partial PUT that omits a provider leaves its stored token intact.
    // An empty string (or JSON null) clears the token back to anonymous access.
    private static void ApplyTokenUpdate(JsonElement body, string requestProperty, JsonObject section, string sectionKey)
    {
        if (!TryGetPropertyIgnoreCase(body, requestProperty, out var provider) ||
            provider.ValueKind != JsonValueKind.Object)
            return;

        if (!TryGetPropertyIgnoreCase(provider, "token", out var token))
            return;

        if (section[sectionKey] is not JsonObject providerSection)
        {
            providerSection = new JsonObject();
            section[sectionKey] = providerSection;
        }

        var value = ToStringValueOrNull(token);
        // Empty string means "clear"; store null so the masked view reads as not-set and git falls
        // back to anonymous access.
        providerSection[TokenKey] = string.IsNullOrEmpty(value) ? null : value;
    }

    // True when the request sets a *new, non-empty* token for the provider block (block present &
    // an object, "token" field present, value a non-empty string) — the exact case ApplyTokenUpdate
    // would persist as a set token. An omitted provider, an omitted token field, or a clearing empty
    // string / null yields false, so those are never probed.
    private static bool TryGetNewToken(JsonElement body, string requestProperty, out string token)
    {
        token = string.Empty;

        if (!TryGetPropertyIgnoreCase(body, requestProperty, out var provider) ||
            provider.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetPropertyIgnoreCase(provider, "token", out var tokenElement))
            return false;

        var value = ToStringValueOrNull(tokenElement);
        if (string.IsNullOrEmpty(value))
            return false;

        token = value;
        return true;
    }

    // GitHub: GET /user with a Bearer token and a User-Agent (GitHub's API rejects requests without one).
    private Task<string?> ProbeGitHubAsync(string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GitHubProbeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Synth");
        return ProbeAsync(request, "GitHub", cancellationToken);
    }

    // GitLab: GET /api/v4/user with a PRIVATE-TOKEN header (gitlab.com only — see class remarks).
    private Task<string?> ProbeGitLabAsync(string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GitLabProbeUrl);
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        return ProbeAsync(request, "GitLab", cancellationToken);
    }

    // Sends the authenticated probe under a short timeout. Returns null when the token authenticates
    // (2xx), or a human-readable reason (for a 400) when it doesn't — a non-2xx response (401/403) or
    // any network failure/timeout. Never persists; this runs before the update.
    private async Task<string?> ProbeAsync(
        HttpRequestMessage request, string provider, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return $"the {provider} token is invalid or lacks API access (received {(int)response.StatusCode}).";

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"the {provider} token probe timed out after {ProbeTimeout.TotalSeconds:0}s.";
        }
        catch (Exception ex)
        {
            return $"the {provider} token could not be verified: {ex.Message}";
        }
        finally
        {
            request.Dispose();
        }
    }

}
