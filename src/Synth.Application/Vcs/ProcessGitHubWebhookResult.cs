namespace Synth.Application.Vcs;

/// <summary>
/// Discriminated result of handling a <see cref="ProcessGitHubWebhookCommand"/>. The controller maps
/// <see cref="Kind.Unauthorized"/> to <c>401</c> (bad/missing signature, or no secret configured —
/// there is no legitimate "unauthenticated webhook" case); every other kind maps to <c>200</c>, since
/// from GitHub's perspective the delivery was received and handled, whether or not it resulted in a
/// reindex (returning non-2xx for "not our event" would just make GitHub retry pointlessly).
/// </summary>
public sealed record ProcessGitHubWebhookResult(ProcessGitHubWebhookResult.Kind Status, string Message)
{
    public enum Kind
    {
        /// <summary>Signature missing/invalid, or no webhook secret configured.</summary>
        Unauthorized,

        /// <summary>Signature was valid, but the event doesn't correspond to a reindex (wrong event
        /// type, unparseable/unknown repository, or a push to a branch that isn't the indexed one).</summary>
        Ignored,

        /// <summary>The matching collection's reindex could not start because one was already running.</summary>
        AlreadyRunning,

        /// <summary>A reindex of the matching collection was started.</summary>
        Started,
    }

    public static ProcessGitHubWebhookResult Unauthorized(string message) => new(Kind.Unauthorized, message);

    public static ProcessGitHubWebhookResult Ignored(string message) => new(Kind.Ignored, message);

    public static ProcessGitHubWebhookResult AlreadyRunning(string message) => new(Kind.AlreadyRunning, message);

    public static ProcessGitHubWebhookResult Started(string collection) =>
        new(Kind.Started, $"Reindexing '{collection}'.");
}
