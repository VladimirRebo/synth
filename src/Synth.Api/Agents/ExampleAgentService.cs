using Microsoft.Agents.AI;

namespace Synth.Api.Agents;

/// <summary>
/// Minimal proof-of-wiring for the Microsoft Agent Framework (MAF): it runs a
/// single <see cref="AIAgent"/> against a string input and returns the agent's
/// text output. The injected agent is built over an offline chat client
/// (<see cref="EchoChatClient"/>), so this runs fully offline. Its only purpose
/// is to prove the MAF package is referenced and its API surface works in this
/// repo — it is not the eventual orchestration design.
/// </summary>
public sealed class ExampleAgentService
{
    private readonly AIAgent _agent;

    public ExampleAgentService(AIAgent agent) => _agent = agent;

    /// <summary>Runs the example agent end-to-end and returns its text reply.</summary>
    public async Task<string> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        var response = await _agent.RunAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Text;
    }
}
