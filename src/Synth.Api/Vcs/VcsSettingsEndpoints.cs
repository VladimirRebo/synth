using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synth.Core.Vcs;
using Synth.Domain.Configuration;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Configuration;

namespace Synth.Api.Vcs;

/// <summary>
/// Maps <c>GET</c>/<c>PUT /settings/vcs</c>: the read/write API over the <c>Vcs</c> config
/// section (<see cref="VcsOptions"/>). Reads report whether a token is set rather than echoing the
/// secret; writes are partial (an absent field is left unchanged, an explicit empty string clears a
/// token) and persist through <see cref="ConfigSectionUpdater"/> so the change is picked up live by
/// <c>IOptionsMonitor&lt;VcsOptions&gt;</c>. No auth/RBAC — Synth is a single local user.
/// <para>
/// A newly-set, non-empty GitHub/GitLab token is <b>probed-before-persist</b> (SYNTH-37): a quick
/// authenticated call against the provider's public API must succeed before the token is saved. A
/// non-2xx response or a network failure is rejected with 400 and nothing is persisted — the same
/// contract as the embedding probe, so an invalid/expired token is caught here instead of surfacing
/// later deep inside a background indexing job. Self-hosted GitLab is out of scope (no configurable
/// host to probe), so GitLab is validated against <c>gitlab.com</c> only.
/// </para>
/// </summary>
public static class VcsSettingsEndpoints
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

    public static IEndpointRouteBuilder MapVcsSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /settings/vcs — current effective VcsOptions, with tokens masked to a set/not-set flag.
        endpoints.MapGet("/settings/vcs",
            (IOptionsMonitor<VcsOptions> options) => Results.Ok(Mask(options.CurrentValue)));

        // PUT /settings/vcs — partial update; returns the same masked shape as GET.
        endpoints.MapPut("/settings/vcs", async (
            JsonElement body,
            ConfigSectionUpdater updater,
            IOptionsMonitor<VcsOptions> options,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Request body must be a JSON object." });

            // Probe a newly-set, non-empty token before persisting so an invalid/expired token is
            // rejected here rather than the next time GitRepoService actually uses it. Cleared or
            // omitted tokens (and workspaceRoot) need no probe — see TryGetNewToken.
            if (TryGetNewToken(body, "github", out var gitHubToken))
            {
                var error = await ProbeGitHubAsync(httpClientFactory, gitHubToken, cancellationToken);
                if (error is not null)
                    return Results.BadRequest(new { error });
            }

            if (TryGetNewToken(body, "gitlab", out var gitLabToken))
            {
                var error = await ProbeGitLabAsync(httpClientFactory, gitLabToken, cancellationToken);
                if (error is not null)
                    return Results.BadRequest(new { error });
            }

            await updater.UpdateSectionAsync(VcsOptions.SectionName, section =>
            {
                // Present & non-null -> set; present & null -> clear; absent -> leave unchanged.
                if (TryGetPropertyIgnoreCase(body, "workspaceRoot", out var workspaceRoot))
                    section[WorkspaceRootKey] = ToStringValueOrNull(workspaceRoot);

                ApplyTokenUpdate(body, "github", section, GitHubKey);
                ApplyTokenUpdate(body, "gitlab", section, GitLabKey);
            }, cancellationToken);

            // The store's Changed event has already reloaded IConfiguration synchronously on save,
            // so CurrentValue reflects the just-persisted values (this is what proves the reload path).
            return Results.Ok(Mask(options.CurrentValue));
        });

        return endpoints;
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
    private static Task<string?> ProbeGitHubAsync(
        IHttpClientFactory httpClientFactory, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GitHubProbeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Synth");
        return ProbeAsync(httpClientFactory, request, "GitHub", cancellationToken);
    }

    // GitLab: GET /api/v4/user with a PRIVATE-TOKEN header (gitlab.com only — see class remarks).
    private static Task<string?> ProbeGitLabAsync(
        IHttpClientFactory httpClientFactory, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GitLabProbeUrl);
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        return ProbeAsync(httpClientFactory, request, "GitLab", cancellationToken);
    }

    // Sends the authenticated probe under a short timeout. Returns null when the token authenticates
    // (2xx), or a human-readable reason (for a 400) when it doesn't — a non-2xx response (401/403) or
    // any network failure/timeout. Never persists; this runs before the update.
    private static async Task<string?> ProbeAsync(
        IHttpClientFactory httpClientFactory, HttpRequestMessage request, string provider, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
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

    private static VcsSettingsResponse Mask(VcsOptions options) => new(
        options.WorkspaceRoot,
        new ProviderTokenStatus(!string.IsNullOrEmpty(options.GitHub?.Token)),
        new ProviderTokenStatus(!string.IsNullOrEmpty(options.GitLab?.Token)));

    private static string? ToStringValueOrNull(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Masked <c>Vcs</c> settings: the raw tokens are never echoed, only whether one is set.</summary>
public sealed record VcsSettingsResponse(string? WorkspaceRoot, ProviderTokenStatus Github, ProviderTokenStatus Gitlab);

/// <summary>Per-provider token status without the secret value.</summary>
public sealed record ProviderTokenStatus(bool TokenSet);
