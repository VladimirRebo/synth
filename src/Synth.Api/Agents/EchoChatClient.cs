using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Synth.Api.Agents;

/// <summary>
/// Offline stand-in for a real LLM. It deterministically echoes the last user
/// message back as the assistant reply, so the Microsoft Agent Framework wiring
/// can be exercised in tests without any live model credentials
/// (Anthropic/OpenAI/Azure) or network access. This is a proof-of-wiring mock,
/// not a production chat client.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var reply = new ChatMessage(ChatRole.Assistant, $"echo: {LastUserText(messages)}");
        return Task.FromResult(new ChatResponse(reply));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, $"echo: {LastUserText(messages)}");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        // Nothing to dispose — the mock holds no unmanaged or network resources.
    }

    private static string LastUserText(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
}
