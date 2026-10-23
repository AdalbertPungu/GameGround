using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace GameGround.AgentFramework;

public static class LocalAnalystModel
{
    public static ChatClientAnalystModel FromEnvironment() => Create(Environment.GetEnvironmentVariable);

    public static ChatClientAnalystModel Create(Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration("GAMEGROUND_ANALYST_ENDPOINT");
        var model = configuration("GAMEGROUND_ANALYST_MODEL");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("GAMEGROUND_ANALYST_ENDPOINT must be an explicit loopback HTTP(S) base URL without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("GAMEGROUND_ANALYST_MODEL must name a locally served model.");
        var version = configuration("GAMEGROUND_ANALYST_MODEL_VERSION");
        var key = configuration("GAMEGROUND_ANALYST_API_KEY");
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        ChatClientAnalystModel? result = null;
        try
        {
            var client = new OpenAI.Chat.ChatClient(model,
                new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "local-no-auth" : key),
                new OpenAIClientOptions
                {
                    Endpoint = uri,
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0)
                }).AsIChatClient();
            result = new ChatClientAnalystModel(client,
                new AnalystModelIdentity("local-openai-compatible", model,
                    string.IsNullOrWhiteSpace(version) ? "unreported" : version), http);
            return result;
        }
        finally
        {
            if (result is null)
                http.Dispose();
        }
    }
}
