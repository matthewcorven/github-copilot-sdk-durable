using Durable;

namespace BandF.Durable;

/// <summary>
/// The four durable documents this scenario stores, owned per turn.
/// </summary>
/// <remarks>
/// The owner id embeds the turn (<see cref="RunView.OwnerFor"/>), so a turn's routing decision,
/// user message, specialist output and control records are separate documents with separate
/// histories. That is what lets the evidence ask "was this turn's routing decision written once?"
/// without reading a transcript, and it keeps a specialist's output readable by a process that has
/// not reopened the specialist's conversation: <see cref="DurableStore.Document"/> takes a kind and
/// an owner id, so the store answers without a live conversation.
/// </remarks>
internal static class Docs
{
    public static readonly DocumentDefinition<RoutingDecision> Routing = new(
        "band-f.routing", DocumentHistory.Latest, ForkPolicy.Initial);

    public static readonly DocumentDefinition<SpecialistOutput> Output = new(
        "band-f.specialist-output", DocumentHistory.Latest, ForkPolicy.Initial);

    public static readonly DocumentDefinition<UserTurnRecord> UserTurn = new(
        "band-f.user-turn", DocumentHistory.Latest, ForkPolicy.Initial);

    /// <summary>
    /// The control-transfer log: who holds the conversation, one revision per commit that changed
    /// it, each carrying the entry it was committed under.
    /// </summary>
    /// <remarks>
    /// Rewindable, because the point is to read a past commit back. Two records written by one call
    /// share an entry id — they were committed in one transaction — which is a property a timestamp
    /// cannot demonstrate. The evidence reads this out of the store rather than off stdout.
    /// </remarks>
    public static readonly DocumentDefinition<WireRecord> Wire = new(
        "band-f.wire", DocumentHistory.Rewindable, ForkPolicy.Initial);
}

/// <summary>
/// The durable view of a run, rebuilt from the store by every process. There is no coordinator
/// variable: every field here is read from a committed record.
/// </summary>
internal sealed class RunView(DurableStore store)
{
    public DurableStore Store { get; } = store;

    public DurableTaskRecord? Task => Store.Tasks.FirstOrDefault(task => task.Name == Names.Task);

    public DelegationCheckpoint Checkpoint =>
        Task is { } task
            ? task.Checkpoint.Deserialize<DelegationCheckpoint>(DurableJson.Options) ?? new DelegationCheckpoint()
            : new DelegationCheckpoint();

    public DelegationInput? Input =>
        Task is { } task ? task.Input.Deserialize<DelegationInput>(DurableJson.Options) : null;

    public bool Terminal => Task?.Status == global::Durable.TaskStatus.Terminal;

    /// <summary>Reads a turn's document, or <c>null</c> when that turn has none.</summary>
    public T? Document<T>(DocumentDefinition<T> definition, int turn) =>
        Store.Document(definition.Kind, OwnerFor(turn))?.Latest is { } latest
            ? latest.Deserialize<T>(DurableJson.Options)
            : default;

    /// <summary>How many versions a turn's document has; a decision is expected to have exactly one.</summary>
    public int Versions<T>(DocumentDefinition<T> definition, int turn) =>
        Store.Document(definition.Kind, OwnerFor(turn))?.Versions.Count ?? 0;

    /// <summary>The owner id a turn's documents live under.</summary>
    public static string OwnerFor(int turn) => $"{Names.Task}/{turn}";

    /// <summary>Every control record in commit order, with the entry it was committed under.</summary>
    public IReadOnlyList<(WireRecord Record, string EntryId, long Sequence)> Wire() =>
        Store.Documents
            .Where(document => document.Kind == Docs.Wire.Kind)
            .SelectMany(document => document.Versions)
            .OrderBy(version => version.Sequence)
            .Select(version => new
            {
                Version = version,
                Value = version.Value.Deserialize<WireRecord>(DurableJson.Options),
            })
            .Where(pair => pair.Value is not null)
            .Select(pair => (pair.Value!, pair.Version.EntryId, pair.Version.Sequence))
            .ToList();
}
