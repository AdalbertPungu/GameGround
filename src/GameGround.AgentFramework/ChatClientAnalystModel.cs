using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameGround.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace GameGround.AgentFramework;

public sealed class ChatClientAnalystModel : IAnalystModel, IDisposable
{
    private static readonly JsonSerializerOptions Serialization = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    };
    private readonly IChatClient client;
    private readonly IDisposable? transportOwner;

    public ChatClientAnalystModel(IChatClient client, AnalystModelIdentity identity)
        : this(client, identity, null) { }

    internal ChatClientAnalystModel(IChatClient client, AnalystModelIdentity identity, IDisposable? transportOwner)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Provider) || string.IsNullOrWhiteSpace(identity.Model)
            || string.IsNullOrWhiteSpace(identity.Version))
            throw new ArgumentException("Provider, model and version metadata are required.", nameof(identity));
        this.client = client;
        Identity = identity;
        this.transportOwner = transportOwner;
    }

    public AnalystModelIdentity Identity { get; }

    public async Task<AnalystModelOutput> AnalyzeAsync(AnalystPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        if (prompt.Version != AnalystSystemPrompt.Version)
            throw new ArgumentException("Unsupported Analyst prompt version.", nameof(prompt));
        // A fresh agent/session prevents conversation state crossing window or match boundaries.
        AIAgent agent = new ChatClientAgent(client, instructions: AnalystSystemPrompt.Text, name: "gameground-analyst");
        var session = await agent.CreateSessionAsync(cancellationToken);
        try
        {
            var response = await agent.RunAsync<AnalystModelOutput>(
                JsonSerializer.Serialize(prompt, Serialization), session,
                serializerOptions: Serialization, cancellationToken: cancellationToken);
            if (response.FinishReason is { } finish && finish != ChatFinishReason.Stop)
                throw new AnalystModelOutputException("Model response did not finish normally.");
            using var document = JsonDocument.Parse(response.Text,
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new AnalystModelOutputException("Expected a structured observation object, not a null or scalar response.");
            return response.Result;
        }
        catch (JsonException)
        {
            throw new AnalystModelOutputException("Model did not return the required structured observations.");
        }
        catch (ClientResultException)
        {
            throw new AgentExecutionException("Local Analyst provider rejected the request; check model and structured-output support.");
        }
        catch (HttpRequestException)
        {
            throw new AgentExecutionException("Local Analyst endpoint is unavailable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentExecutionException("Local Analyst provider request timed out.");
        }
    }

    public void Dispose()
    {
        client.Dispose();
        transportOwner?.Dispose();
    }
}
