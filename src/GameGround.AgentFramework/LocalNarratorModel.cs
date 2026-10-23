using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace GameGround.AgentFramework;

public static class LocalNarratorModel
{
    public static ChatClientNarratorModel FromEnvironment() => Create(Environment.GetEnvironmentVariable);

    public static ChatClientNarratorModel Create(Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Uri.TryCreate(configuration("GAMEGROUND_NARRATOR_ENDPOINT"), UriKind.Absolute, out var endpoint)
            || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https")
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("GAMEGROUND_NARRATOR_ENDPOINT must be an explicit loopback HTTP(S) base URL without credentials, query or fragment.");
        var model = configuration("GAMEGROUND_NARRATOR_MODEL");
        if (!NarratorLimits.ValidIdentity(model))
            throw new InvalidOperationException("GAMEGROUND_NARRATOR_MODEL must name a locally served model (at most 256 characters).");
        var version = configuration("GAMEGROUND_NARRATOR_MODEL_VERSION");
        if (version is not null && !NarratorLimits.ValidIdentity(version))
            throw new InvalidOperationException("GAMEGROUND_NARRATOR_MODEL_VERSION must be nonempty and at most 256 characters.");
        var timeoutText = configuration("GAMEGROUND_NARRATOR_TIMEOUT_SECONDS");
        var seconds = 5d;
        if (timeoutText is not null && (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture,
            out seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 300))
            throw new InvalidOperationException("GAMEGROUND_NARRATOR_TIMEOUT_SECONDS must be positive and at most 300.");
        var key = configuration("GAMEGROUND_NARRATOR_API_KEY");
        var http = new HttpClient(new NarratorResponseLimitHandler(
            new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        ChatClientNarratorModel? result = null;
        try
        {
            var client = new OpenAI.Chat.ChatClient(model,
                new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "local-no-auth" : key),
                new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0)
                }).AsIChatClient();
            result = new ChatClientNarratorModel(client,
                new NarratorModelIdentity("local-openai-compatible", model, version ?? "unreported"),
                TimeSpan.FromSeconds(seconds), http);
            return result;
        }
        finally
        {
            if (result is null)
                http.Dispose();
        }
    }
}
