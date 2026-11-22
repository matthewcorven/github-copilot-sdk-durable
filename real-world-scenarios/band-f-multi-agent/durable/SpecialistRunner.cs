using System.ComponentModel;
using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace BandF.Durable;

/// <summary>
/// Runs one specialist. Each owns one tool, with the baseline's exact result string, and is handed
/// its own prior output rather than the conversation that produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the specialist is given.</b> Its instructions (the sample's), its one tool, the request the
/// checkpoint recorded, and the <see cref="SpecialistOutput"/> records it committed on earlier turns.
/// It is never given a transcript, and it is never asked which specialist should act — the
/// checkpoint already decided that. A resumed specialist therefore starts from the same three things
/// a first attempt did, plus its own prior work.
/// </para>
/// <para>
/// <b>How the output becomes a record.</b> The tool's result is captured as the tool executes, so the
/// <see cref="SpecialistOutput"/> carries the domain fact beside the agent's prose. A reader of the
/// store can check the answer against the tool's own words instead of trusting the model to have
/// reported them.
/// </para>
/// </remarks>
internal sealed class SpecialistRunner
{
    private readonly DurableHarness _harness;
    private readonly string _model;
    private readonly ToolResults _results;
    private readonly ConsoleLog _log;

    public SpecialistRunner(DurableHarness harness, string model, ToolResults results, ConsoleLog log)
    {
        _harness = harness;
        _model = model;
        _results = results;
        _log = log;
    }

    /// <summary>
    /// Installs one extension per specialist. Each contributes only its own tool, so the extension a
    /// checkpoint's specialist is granted cannot reach another specialist's capability.
    /// </summary>
    public static void Install(ExtensionRegistry registry, ToolResults results)
    {
        foreach (var specialist in Agents.Specialists)
        {
            registry.Install(new Extension(ExtensionName(specialist), tools: [ToolFor(specialist, results)]));
        }
    }

    /// <summary>The extension a specialist is granted.</summary>
    public static string ExtensionName(string specialist) =>
        $"specialist.{specialist.Replace("_agent", string.Empty, StringComparison.Ordinal)}";

    /// <summary>
    /// Produces the specialist's output for a turn. The record is returned rather than written: the
    /// caller commits it in the same transaction as the checkpoint that says the turn may advance.
    /// </summary>
    public async Task<SpecialistOutput> ProduceAsync(
        int turn,
        string specialist,
        string request,
        CancellationToken cancellationToken)
    {
        var prior = PriorOutputs(specialist);

        // The line the "domain output is a document" claim rests on: what the specialist is handed
        // came out of the store, by kind and owner, not out of a transcript.
        _log.Line(prior.Count == 0
            ? $"[specialist] {specialist} was handed no prior output from the store"
            : $"[specialist] {specialist} was handed {prior.Count} prior output(s) from the store: "
                + string.Join(", ", prior.Select(output => $"turn {output.Turn} ({output.Tool})")));

        var conversation = await _harness.CreateConversationAsync(
            new AgentChoices
            {
                Model = _model,
                Extensions = [ExtensionName(specialist)],
                Permissions = [$"custom-tool:{Agents.Tool(specialist)}"],
                Instructions = Agents.Instructions(specialist),
            },
            cancellationToken: cancellationToken);

        var prompt = Prompt(turn, specialist, request, prior);
        var answer = await conversation.SubmitAsync(prompt, cancellationToken);

        return new SpecialistOutput
        {
            Turn = turn,
            Specialist = specialist,
            Answer = answer?.Data.Content ?? "(the specialist produced no answer)",
            Tool = Agents.Tool(specialist),
            ToolResult = _results.TakeLast(specialist),
            ModelTurns = conversation.TurnModels.Count,
            Conversation = conversation.Id,
            ProducedBy = $"pid {Environment.ProcessId}",
        };
    }

    /// <summary>
    /// The specialist's own committed outputs from earlier turns, oldest first, capped at
    /// <see cref="Names.PriorOutputWindow"/>.
    /// </summary>
    /// <remarks>
    /// This is design point 3: a resumed agent is handed its own prior output as a value, read from
    /// the store by kind and owner, without reopening the conversation that produced it. The window
    /// keeps the prompt bounded and is stated in the prompt, so the model knows it is looking at the
    /// last few rather than at everything.
    /// </remarks>
    private List<SpecialistOutput> PriorOutputs(string specialist) =>
        _harness.Store.Documents
            .Where(document => document.Kind == Docs.Output.Kind)
            .Select(document => document.Latest?.Deserialize<SpecialistOutput>(DurableJson.Options))
            .Where(output => output is not null && output.Specialist == specialist)
            .Select(output => output!)
            .OrderBy(output => output.Turn)
            .TakeLast(Names.PriorOutputWindow)
            .ToList();

    private static string Prompt(int turn, string specialist, string request, List<SpecialistOutput> prior)
    {
        var text = $"""
            A routing agent handed this conversation to you with this request:

            {request}

            This is turn {turn} of the run. Use {Agents.Tool(specialist)} and tell the customer what
            it returned.
            """;

        if (prior.Count == 0)
        {
            return text;
        }

        var priorText = string.Join(
            "\n",
            prior.Select(output => $"- turn {output.Turn}: {output.Answer}"
                + (output.ToolResult is null ? string.Empty : $" ({output.Tool} returned: {output.ToolResult})")));

        return $"""
            {text}

            Your own earlier work for this customer, as you recorded it (the last {prior.Count} of at
            most {Names.PriorOutputWindow}):

            {priorText}

            Continue from it. Do not repeat work the record says you already did.
            """;
    }

    private static AIFunction ToolFor(string specialist, ToolResults results) => specialist switch
    {
        Agents.Refund => CopilotTool.DefineTool(
            ([Description("Order number to process refund for")] string orderNumber) =>
            {
                var value = $"Refund processed successfully for order {orderNumber}.";
                results.Record(Agents.Refund, value);
                return value;
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = Agents.Tool(Agents.Refund),
                Description = "Process a refund for a given order number.",
            }),

        Agents.Order => CopilotTool.DefineTool(
            ([Description("Order number to check status for")] string orderNumber) =>
            {
                var value = $"Order {orderNumber} is currently being processed and will ship in 2 business days.";
                results.Record(Agents.Order, value);
                return value;
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = Agents.Tool(Agents.Order),
                Description = "Check the status of a given order number.",
            }),

        Agents.Returns => CopilotTool.DefineTool(
            ([Description("Order number to process return for")] string orderNumber) =>
            {
                var value = $"Return initiated successfully for order {orderNumber}. "
                    + "You will receive return instructions via email.";
                results.Record(Agents.Returns, value);
                return value;
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = Agents.Tool(Agents.Returns),
                Description = "Process a return for a given order number.",
            }),

        _ => throw new ArgumentOutOfRangeException(nameof(specialist), specialist, "not a specialist"),
    };

    /// <summary>
    /// The tools' results, per specialist, in call order. The runner reads the last one after the
    /// turn, because a turn may call the tool more than once and the last call is what the answer is
    /// about.
    /// </summary>
    internal sealed class ToolResults
    {
        private readonly Dictionary<string, List<string>> _byAgent = [];

        public void Record(string specialist, string result)
        {
            lock (_byAgent)
            {
                if (!_byAgent.TryGetValue(specialist, out var results))
                {
                    results = [];
                    _byAgent[specialist] = results;
                }

                results.Add(result);
            }
        }

        public string? TakeLast(string specialist)
        {
            lock (_byAgent)
            {
                if (!_byAgent.TryGetValue(specialist, out var results) || results.Count == 0)
                {
                    return null;
                }

                var value = results[^1];
                results.Clear();
                return value;
            }
        }
    }
}
