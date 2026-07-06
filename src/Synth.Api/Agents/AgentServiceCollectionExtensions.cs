using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Synth.Api.Agents;

/// <summary>
/// DI wiring for the minimal Microsoft Agent Framework example. This proves the
/// framework is referenced and buildable in Synth.Api; it deliberately does not
/// touch the existing hand-rolled agent loop (scripts/loop.sh + maker/checker).
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddSynthAgents(this IServiceCollection services)
    {
        // Offline chat client: deterministic, no model credentials, no network.
        services.AddSingleton<IChatClient, EchoChatClient>();

        // One minimal MAF agent built over the offline client.
        services.AddSingleton<AIAgent>(sp => new ChatClientAgent(
            sp.GetRequiredService<IChatClient>(),
            instructions: "Echo the user's message back to them.",
            name: "synth-echo"));

        services.AddSingleton<ExampleAgentService>();
        return services;
    }
}
