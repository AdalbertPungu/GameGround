using System.Collections.Immutable;
using GameGround.Agents;
using GameGround.Core;
using GameGround.Intelligence;
using static GameGround.Tests.Intelligence.IntelligenceTestData;

namespace GameGround.Tests.Agents;

internal static class AgentTestData
{
    internal static AgentIdentity Analyst { get; } = new("test-analyst", AgentRole.Analyst);
    internal static AgentIdentity Narrator { get; } = new("test-narrator", AgentRole.Narrator);
    internal static AgentContext Context(int cutoff = 20) => AgentContextFactory.Through(Reference(), cutoff);

    internal static Claim PassClaim(AgentContext context, AgentIdentity? producer = null)
    {
        var fact = context.Facts.Single(item => item.Kind == FactKind.PassCompletion && item.Team == new TeamId(2));
        return ClaimText.ForFact(context, fact.Id, new ClaimId(1), producer ?? Analyst);
    }

    internal static LocalOrchestrator Workflow(
        ILocalAgent analyst, ILocalAgent? narrator = null, int revisions = 1, TimeSpan? timeout = null) =>
        new([analyst], narrator ?? new SimulatedNarrator(),
            new WorkflowOptions(revisions, timeout ?? TimeSpan.FromSeconds(5)));

    internal sealed class ScriptedAgent(
        AgentIdentity identity,
        Func<AgentRequest, CancellationToken, Task<AgentOutput>> action) : ILocalAgent
    {
        public AgentIdentity Identity { get; } = identity;
        public List<AgentRequest> Requests { get; } = [];
        public Task<AgentOutput> GenerateAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return action(request, cancellationToken);
        }
    }

    internal static ScriptedAgent Producing(
        AgentIdentity identity, Func<AgentRequest, ImmutableArray<Claim>> claims) =>
        new(identity, (request, _) => Task.FromResult(new AgentOutput(claims(request))));
}
