using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;
using Synth.Api.Agents;

namespace Synth.Api.Tests;

/// <summary>
/// Proves the Microsoft Agent Framework wiring (SYNTH-5): a MAF agent runs
/// end-to-end against an offline mock chat client, with no live LLM credentials
/// and no network access.
/// </summary>
public class ExampleAgentTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public ExampleAgentTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Example_agent_runs_offline_and_echoes_input()
    {
        // Build the agent directly from the offline pieces — no host, no network.
        var agent = new ChatClientAgent(
            new EchoChatClient(),
            instructions: "Echo the user's message back to them.",
            name: "synth-echo");
        var service = new ExampleAgentService(agent);

        var result = await service.RunAsync("hello synth");

        Assert.Equal("echo: hello synth", result);
    }

    [Fact]
    public void AddSynthAgents_registers_the_example_agent_in_DI()
    {
        // Proves the DI wiring resolves the MAF agent and its service.
        using var scope = _factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<ExampleAgentService>();
        var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();

        Assert.NotNull(service);
        Assert.IsType<ChatClientAgent>(agent);
    }
}
