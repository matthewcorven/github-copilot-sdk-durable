using System.ComponentModel;
using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace BandF.Durable;

/// <summary>How a routing decision is produced.</summary>
internal enum RoutingMode
{
    /// <summary>The coordinator agent decides, by calling the delegation tool.</summary>
    Model,

    /// <summary>The scenario decides from the user's words, with the same record and the same commit.</summary>
    Scripted,
}

/// <summary>
/// The coordinator's routing decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tool call, not prose.</b> A decision becomes a durable value only if the model emits it as
/// one, so the coordinator's entire capability is a single tool, <c>delegate_to_specialist</c>. Its
/// prose is not consulted: a coordinator that answers in words without calling the tool has not
/// routed, and the run fails naming that rather than guessing a specialist from a sentence.
/// </para>
/// <para>
/// <b>Two ways to decide, one way to record.</b> <see cref="RoutingMode.Model"/> asks the agent.
/// <see cref="RoutingMode.Scripted"/> decides from the user's words with no model call at all. Both
/// return a <see cref="RoutingDecision"/>, and the caller commits it through the same document and
/// the same memoization check, so the durable behaviour the band is about is identical on both paths
/// and only the quality of the decision differs. The recorded <c>Source</c> says which path produced
/// it, so no run can be read as having asked a model when it did not.
/// </para>
/// </remarks>
internal sealed class Router
{
    private readonly DurableHarness _harness;
    private readonly RoutingMode _mode;
    private readonly string _model;

    public Router(DurableHarness harness, RoutingMode mode, string model)
    {
        _harness = harness;
        _mode = mode;
        _model = model;
    }

    /// <summary>Installs the coordinator's one tool and returns where its arguments land.</summary>
    public static DecisionSink Install(ExtensionRegistry registry)
    {
        var sink = new DecisionSink();
        registry.Install(new Extension("handoff", tools: [Tool(sink)]));
        return sink;
    }

    /// <summary>The coordinator's choices: one tool, fail-closed to it, and no other capability.</summary>
    public AgentChoices Choices(string? model = null) => new()
    {
        Model = model ?? _model,
        Extensions = ["handoff"],
        Permissions = [$"custom-tool:{Names.DecisionTool}"],
        Instructions =
            "You are frontline support triage. Route the customer's issue to the appropriate "
            + "specialist by calling delegate_to_specialist exactly once, then stop. The specialists "
            + "are refund_agent (refunds), order_agent (order and shipping status) and return_agent "
            + "(returns, damaged items, packaging). Do not answer the customer yourself.",
    };

    /// <summary>Decides which specialist takes the turn, and says which path decided.</summary>
    public async Task<RoutingDecision> DecideAsync(int turn, string message, CancellationToken cancellationToken)
    {
        if (_mode == RoutingMode.Scripted)
        {
            var (scriptedSpecialist, scriptedRequest) = Scripted(message);
            return new RoutingDecision
            {
                Turn = turn,
                Specialist = scriptedSpecialist,
                Request = $"{scriptedRequest}: {message}",
                Model = null,
                Source = "scripted",
            };
        }

        var sink = Install(_harness.Registry);
        var conversation = await _harness.CreateConversationAsync(Choices(), cancellationToken: cancellationToken);
        var answer = await conversation.SubmitAsync(message, cancellationToken);

        var calls = sink.Calls;
        if (calls.Count == 0)
        {
            throw new InvalidOperationException(
                $"the coordinator ({Agents.Coordinator}) answered without calling {Names.DecisionTool}, so no "
                + $"agent was named for turn {turn}. Its answer was: {answer?.Data.Content ?? "(none)"}");
        }

        var distinct = calls.Select(call => call.Specialist).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            throw new InvalidOperationException(
                $"the coordinator named more than one specialist for turn {turn}: {string.Join(", ", distinct)}");
        }

        var specialist = distinct[0];
        if (!Agents.IsSpecialist(specialist))
        {
            throw new InvalidOperationException(
                $"the coordinator named '{specialist}' for turn {turn}, which is not one of "
                + string.Join(", ", Agents.Specialists));
        }

        // The runtime's own report, not the requested model: a runtime that substitutes one is
        // visible in the record instead of being attributed to the request.
        var reported = await conversation.WaitForEffectiveModelAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return new RoutingDecision
        {
            Turn = turn,
            Specialist = specialist,
            Request = calls[0].Request,
            Model = reported.Model,
            Source = "model",
        };
    }

    /// <summary>
    /// The deterministic decision: what the user's words ask for. Deliberately three keywords,
    /// because it is not the subject of the band and a cleverer one would only invite a comparison of
    /// its quality with the model's.
    /// </summary>
    /// <remarks>
    /// <c>BAND_F_FLIP=1</c> reverses the answer, and exists for one purpose: to make "the model
    /// answered differently the second time" a repeatable fact rather than a hope about a
    /// temperature setting. A resumed turn routed with the flip on would name a different specialist
    /// from the one the first attempt committed — which is exactly the divergence the memoization
    /// exists to make impossible. The evidence runs it both ways.
    /// </remarks>
    private static (string Specialist, string Request) Scripted(string message)
    {
        var answer = ScriptedAnswer(message);
        if (Environment.GetEnvironmentVariable("BAND_F_FLIP") is not "1")
        {
            return answer;
        }

        var flipped = answer.Specialist switch
        {
            Agents.Refund => Agents.Returns,
            Agents.Returns => Agents.Order,
            _ => Agents.Refund,
        };
        return (flipped, "a second, different answer");
    }

    private static (string Specialist, string Request) ScriptedAnswer(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("refund", StringComparison.Ordinal))
        {
            return (Agents.Refund, "process the refund");
        }

        if (text.Contains("return", StringComparison.Ordinal) || text.Contains("damaged", StringComparison.Ordinal))
        {
            return (Agents.Returns, "start the return");
        }

        return (Agents.Order, "look up the order");
    }

    private static AIFunction Tool(DecisionSink sink) =>
        CopilotTool.DefineTool(
            ([Description("Specialist to hand the conversation to: refund_agent, order_agent or return_agent.")]
             string specialist,
             [Description("The task for the specialist, in your own words.")]
             string request) =>
            {
                sink.Record(specialist, request);
                return $"Handed the conversation to {specialist}.";
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = Names.DecisionTool,
                Description = "Hand the customer's conversation to one specialist and stop.",
            });

    /// <summary>
    /// Where the tool's arguments land. A turn can call the tool more than once; the caller treats
    /// two different answers as a contradiction rather than picking one.
    /// </summary>
    internal sealed class DecisionSink
    {
        private readonly List<(string Specialist, string Request)> _calls = [];

        public void Record(string specialist, string request)
        {
            lock (_calls)
            {
                _calls.Add((specialist, request));
            }
        }

        public IReadOnlyList<(string Specialist, string Request)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToList();
                }
            }
        }
    }
}
