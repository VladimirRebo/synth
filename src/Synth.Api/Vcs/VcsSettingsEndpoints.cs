using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synth.Api.Configuration;
using Synth.Core.Vcs;

namespace Synth.Api.Vcs;

/// <summary>
/// Maps <c>GET</c>/<c>PUT /api/settings/vcs</c>: the read/write API over the <c>Vcs</c> config
/// section (<see cref="VcsOptions"/>). Reads report whether a token is set rather than echoing the
/// secret; writes are partial (an absent field is left unchanged, an explicit empty string clears a
/// token) and persist through <see cref="ConfigSectionUpdater"/> so the change is picked up live by
/// <c>IOptionsMonitor&lt;VcsOptions&gt;</c>. No auth/RBAC — Synth is a single local user.
/// </summary>
public static class VcsSettingsEndpoints
{
    private const string WorkspaceRootKey = "WorkspaceRoot";
    private const string GitHubKey = "GitHub";
    private const string GitLabKey = "GitLab";
    private const string TokenKey = "Token";

    public static IEndpointRouteBuilder MapVcsSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /api/settings/vcs — current effective VcsOptions, with tokens masked to a set/not-set flag.
        endpoints.MapGet("/api/settings/vcs",
            (IOptionsMonitor<VcsOptions> options) => Results.Ok(Mask(options.CurrentValue)));

        // PUT /api/settings/vcs — partial update; returns the same masked shape as GET.
        endpoints.MapPut("/api/settings/vcs", async (
            JsonElement body,
            ConfigSectionUpdater updater,
            IOptionsMonitor<VcsOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Request body must be a JSON object." });

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
