using System.ClientModel;
using System.Text;
using System.Text.Json;
using GameGround.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework;

public sealed class ChatClientNarratorModel : INarratorModel, IDisposable
{
    private readonly IChatClient client;
    private readonly IDisposable? transportOwner;
    private readonly TimeSpan timeout;

    public ChatClientNarratorModel(
        IChatClient client, NarratorModelIdentity identity, TimeSpan? timeout = null)
        : this(client, identity, timeout ?? TimeSpan.FromSeconds(5), null) { }

    internal ChatClientNarratorModel(
        IChatClient client, NarratorModelIdentity identity, TimeSpan timeout, IDisposable? transportOwner)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        if (!NarratorLimits.ValidIdentity(identity.Provider) || !NarratorLimits.ValidIdentity(identity.Model)
            || !NarratorLimits.ValidIdentity(identity.Version))
            throw new ArgumentException("Bounded provider, model and version metadata are required.", nameof(identity));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Use a positive request timeout up to five minutes.");
        this.client = client;
        Identity = identity;
        this.timeout = timeout;
        this.transportOwner = transportOwner;
    }

    public NarratorModelIdentity Identity { get; }

    public async Task<NarratorModelOutput> NarrateAsync(NarratorPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        if (prompt.Version != NarratorSystemPrompt.Version || prompt.Observations.IsDefaultOrEmpty
            || prompt.Observations.Length > NarratorLimits.MaxObservations
            || prompt.Feedback.IsDefault || prompt.Feedback.Length > NarratorLimits.MaxClaims * 6
            || prompt.Attempt is < 0 or > 1)
            throw new AgentExecutionException("Narrator prompt violates the bounded request contract.");
        var text = JsonSerializer.Serialize(prompt, NarratorLimits.Serialization);
        if (Encoding.UTF8.GetByteCount(text) > NarratorLimits.MaxPromptBytes)
            throw new AgentExecutionException("Narrator prompt exceeds the input size limit.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        AIAgent agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "gameground-narrator",
            ChatOptions = new ChatOptions
            {
                Instructions = NarratorSystemPrompt.Text,
                MaxOutputTokens = NarratorLimits.MaxOutputTokens
            }
        });
        try
        {
            var session = await agent.CreateSessionAsync(linked.Token);
            var response = await agent.RunAsync<NarratorModelOutput>(text, session,
                serializerOptions: NarratorLimits.Serialization, cancellationToken: linked.Token)
                .WaitAsync(timeout, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            if (response.FinishReason != ChatFinishReason.Stop)
                throw new NarratorOutputException(NarratorRejection.InvalidStructure);
            if (Encoding.UTF8.GetByteCount(response.Text) > NarratorLimits.MaxResponseBytes)
                throw new NarratorOutputException(NarratorRejection.SizeLimit);
            using var document = JsonDocument.Parse(response.Text,
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new NarratorOutputException(NarratorRejection.InvalidStructure);
            return response.Result;
        }
        catch (JsonException)
        {
            throw new NarratorOutputException(NarratorRejection.InvalidStructure);
        }
        catch (ClientResultException)
        {
            throw new AgentExecutionException("Local Narrator provider rejected the request; check model and structured-output support.");
        }
        catch (HttpRequestException)
        {
            throw new AgentExecutionException("Local Narrator endpoint is unavailable.");
        }
        catch (IOException)
        {
            throw new AgentExecutionException("Local Narrator transport could not read the response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentExecutionException("Local Narrator provider request timed out.");
        }
        catch (TimeoutException)
        {
            throw new AgentExecutionException("Local Narrator provider request timed out.");
        }
        finally
        {
            linked.Cancel();
        }
    }

    public void Dispose()
    {
        client.Dispose();
        transportOwner?.Dispose();
    }
}
