using System.Text.Json;
using Durable;

namespace BandGDurable;

/// <summary>How many model calls a run has been asked to make, and how many the store has recorded.</summary>
public sealed class ModelLedger
{
    /// <summary>
    /// The document definition, with an initial value. <c>tx.Doc</c> hands back the definition's
    /// initial value the first time a document is written; a definition with none hands back the
    /// default of the value type, which for a class is <see langword="null"/>.
    /// </summary>
    public static readonly DocumentDefinition<ModelLedger> Definition =
        new(Brief.LedgerKind, initial: () => new ModelLedger());

    /// <summary>One entry per call the run actually made, in order.</summary>
    public List<ModelCall> Calls { get; set; } = [];

    /// <summary>How many calls were served without the model because a memo already held the answer.</summary>
    public int MemoHits { get; set; }
}

/// <summary>
/// The steps that call a model. Every one of them is a memo read, then a turn, then a memo write,
/// with the call counted in the same commit that records the answer.
/// </summary>
/// <remarks>
/// The counter lives here, next to the effect it counts, and both are durable. A step that already
/// has a memo does not call the model at all, so "a resume does not re-call the model for completed
/// steps" is not something this file asserts — it is the shape of the code, and
/// <see cref="CallAsync"/> returns which of the two paths it took so the run can print it.
/// <para>
/// The turn itself is a real Copilot SDK turn through the durable harness: admission, session
/// send, assistant message, turn checkpoint. Nothing here stands in for the SDK, and nothing here
/// stands in for the store. What the scenario supplies is the <em>answer</em>: when
/// <see cref="DeterministicModel"/> is set, the assistant text is synthesised from the prompt
/// after the turn returns. The transport, the admission record, and the prompt all really crossed
/// the boundary, so a fixture or a live model changes which bytes came back and nothing else.
/// </para>
/// </remarks>
public sealed class ModelGateway
{
    private readonly DurableHarness _harness;
    private readonly DurableStore _store;
    private readonly string _workingDirectory;

    /// <summary>Creates a gateway over the harness the workflow runs on.</summary>
    /// <param name="harness">The harness whose conversations the turns run in.</param>
    /// <param name="workingDirectory">The scene the turns are told they are working in.</param>
    public ModelGateway(DurableHarness harness, string workingDirectory)
    {
        _harness = harness;
        _store = harness.Store;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// The document owner: the workflow task's id, so the ledger belongs to the run.
    /// </summary>
    /// <remarks>
    /// Set once the task exists. A process that creates the run has to create the task before it
    /// knows the id, so the gateway is built first and pointed at the run second — and no phase
    /// runs until both have happened.
    /// </remarks>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// When set, every turn's answer is replaced by this function of the prompt after the turn
    /// returns. The scenario sets it; nothing in <c>lib/</c> knows it exists.
    /// </summary>
    public Func<string, string>? DeterministicModel { get; set; }

    /// <summary>
    /// When set, the step ignores the memo it finds and calls the model anyway.
    /// </summary>
    /// <remarks>
    /// This is the before/after control for the memoization claim, and it exists so the claim is
    /// measured rather than asserted: the same two-process flow with this set makes one call per
    /// step attempt, so the difference in the ledger is the memo's whole contribution. Nothing else
    /// changes — the memo is still written, so the store's shape is the same either way.
    /// </remarks>
    public bool BypassMemo { get; set; }

    /// <summary>The label written into each ledger entry: <c>deterministic</c> or <c>live</c>.</summary>
    public string ServedBy => DeterministicModel is null ? "live" : "deterministic";

    /// <summary>The ledger as the store holds it.</summary>
    public ModelLedger Read()
    {
        var state = _store.Document(Brief.LedgerKind, Owner);
        return state?.Latest is { } latest
            ? latest.Deserialize<ModelLedger>(DurableJson.Options) ?? new ModelLedger()
            : new ModelLedger();
    }

    /// <summary>
    /// Runs one step. Returns the answer and whether it came from the memo, so a caller can print
    /// which happened rather than infer it.
    /// </summary>
    /// <param name="runtime">The task runtime, whose memo is the guard.</param>
    /// <param name="step">The step label, e.g. <c>research/1/steps/01.md</c>.</param>
    /// <param name="prompt">The prompt to send when the memo does not already hold an answer.</param>
    public async Task<(string Answer, bool FromMemo)> CallAsync(
        TaskRuntime runtime,
        string step,
        string prompt)
    {
        // The memo key is the step's position in the run, never a timestamp, a conversation id, or
        // a digest of the prompt: all three differ between two attempts at the same step, which is
        // exactly how a memo key silently duplicates a call.
        var key = $"model/{step}";
        var existing = BypassMemo ? null : await runtime.MemoAsync(key);
        if (existing is { } memo)
        {
            var recorded = memo.Deserialize<ModelCall>(DurableJson.Options)!;
            await CountMemoHitAsync();
            return (recorded.Answer, true);
        }

        // A fresh conversation per call keeps each turn's context to its own prompt, the way the
        // baseline's participants each have their own agent.
        var conversation = await _harness.CreateConversationAsync(new AgentChoices
        {
            Model = ModelId,
            WorkingDirectory = _workingDirectory,
            Instructions = Instructions,
        });

        var message = await conversation.SubmitAsync(prompt);
        var answer = message?.Data.Content ?? string.Empty;
        if (DeterministicModel is { } answerFor)
        {
            answer = answerFor(prompt);
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException($"the {step} turn returned no assistant text");
        }

        var call = new ModelCall
        {
            Step = step,
            Prompt = prompt,
            Answer = answer,
            ConversationId = conversation.Id,
            ServedBy = ServedBy,
        };

        // The memo and the count commit together with the task's own checkpoint, in the phase's
        // own transaction: a call that happened is never uncounted, and a count never names a call
        // that did not happen.
        await runtime.MemoAsync(key, DurableJson.ToElement(call));
        await CountCallAsync(call);

        return (answer, false);
    }

    /// <summary>The model id every turn asks for.</summary>
    public const string ModelId = "replay-model";

    /// <summary>The instructions a turn runs under, which the baseline gives each participant.</summary>
    public const string Instructions =
        "You are a participant in a two-agent research and writing team. Answer in two sentences or "
        + "fewer, and never ask a question back.";

    private async Task CountCallAsync(ModelCall call)
    {
        await _store.CommitAsync(tx =>
        {
            var draft = tx.Doc(ModelLedger.Definition, Owner);
            draft.Value.Calls.Add(call);
            draft.Save(tx.AppendEntry(Owner, "model-call"));
            return Task.CompletedTask;
        });
    }

    private async Task CountMemoHitAsync()
    {
        await _store.CommitAsync(tx =>
        {
            var draft = tx.Doc(ModelLedger.Definition, Owner);
            draft.Value.MemoHits++;
            draft.Save(tx.AppendEntry(Owner, "memo-hit"));
            return Task.CompletedTask;
        });
    }
}
