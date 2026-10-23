namespace GameGround.AgentFramework;

public sealed class LocalModelEvaluationConfiguration : IDisposable
{
    private LocalModelEvaluationConfiguration(
        ChatClientAnalystModel analyst, ChatClientNarratorModel narrator,
        string analystEndpointIdentity, string narratorEndpointIdentity)
    {
        Analyst = analyst;
        Narrator = narrator;
        AnalystEndpointIdentity = analystEndpointIdentity;
        NarratorEndpointIdentity = narratorEndpointIdentity;
    }

    public ChatClientAnalystModel Analyst { get; }
    public ChatClientNarratorModel Narrator { get; }
    public string AnalystEndpointIdentity { get; }
    public string NarratorEndpointIdentity { get; }

    public static LocalModelEvaluationConfiguration FromEnvironment() => Create(Environment.GetEnvironmentVariable);

    public static LocalModelEvaluationConfiguration Create(Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var analystEndpoint = Required("GAMEGROUND_ANALYST_ENDPOINT");
        var analystModel = Required("GAMEGROUND_ANALYST_MODEL");
        var narratorEndpoint = Required("GAMEGROUND_NARRATOR_ENDPOINT");
        var narratorModel = Required("GAMEGROUND_NARRATOR_MODEL");
        ValidateLabel("GAMEGROUND_ANALYST_MODEL", analystModel);
        ValidateLabel("GAMEGROUND_NARRATOR_MODEL", narratorModel);
        ValidateOptionalLabel("GAMEGROUND_ANALYST_MODEL_VERSION", configuration("GAMEGROUND_ANALYST_MODEL_VERSION"));
        ValidateOptionalLabel("GAMEGROUND_NARRATOR_MODEL_VERSION", configuration("GAMEGROUND_NARRATOR_MODEL_VERSION"));
        var analystEndpointIdentity = ValidateEndpoint("GAMEGROUND_ANALYST_ENDPOINT", analystEndpoint);
        var narratorEndpointIdentity = ValidateEndpoint("GAMEGROUND_NARRATOR_ENDPOINT", narratorEndpoint);

        ChatClientAnalystModel? analyst = null;
        try
        {
            analyst = LocalAnalystModel.Create(configuration);
            var narrator = LocalNarratorModel.Create(configuration);
            return new LocalModelEvaluationConfiguration(analyst, narrator,
                analystEndpointIdentity, narratorEndpointIdentity);
        }
        catch
        {
            analyst?.Dispose();
            throw;
        }

        string Required(string name)
        {
            var value = configuration(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"{name} is required for the explicitly invoked local model evaluation.");
            return value;
        }
    }

    public void Dispose()
    {
        Narrator.Dispose();
        Analyst.Dispose();
    }

    private static void ValidateLabel(string name, string value)
    {
        if (!NarratorLimits.ValidIdentity(value))
            throw new InvalidOperationException($"{name} must be nonempty and at most {NarratorLimits.MaxIdentityCharacters} characters.");
    }

    private static void ValidateOptionalLabel(string name, string? value)
    {
        if (value is not null && !NarratorLimits.ValidIdentity(value))
            throw new InvalidOperationException($"{name} must be nonempty and at most {NarratorLimits.MaxIdentityCharacters} characters when set.");
    }

    private static string ValidateEndpoint(string name, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException($"{name} must be an explicit loopback HTTP(S) endpoint without credentials, query or fragment.");
        return endpoint.GetLeftPart(UriPartial.Authority);
    }
}
