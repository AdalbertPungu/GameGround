using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework.Tests;

internal sealed class ScriptedChatClient(
    params Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>[] responses) : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _responses = new(responses);

    public int CallCount { get; private set; }
    public int RemainingResponses => _responses.Count;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException("No scripted response remains. This client cannot call a model.");

        CallCount++;
        return Task.FromResult(response(messages.ToArray(), options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This API spike scripts non-streaming responses only.");

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(ChatClientMetadata))
            return new ChatClientMetadata("local-scripted");
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }
}
