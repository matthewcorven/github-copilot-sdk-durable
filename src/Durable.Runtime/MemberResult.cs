using System.Text.Json;
using System.Text.Json.Serialization;
using Durable;

namespace Durable.Runtime;

/// <summary>
/// The result of a <c>member</c> invocation: the transition as the store now holds it, or the
/// refusal that kept it unchanged.
/// </summary>
/// <remarks>
/// <see cref="Outcome"/> is the vocabulary the exit code maps from, so a caller that ignores the
/// JSON still sees the difference between "the transition landed", "the request is invalid for
/// the current state", and "the stored state cannot be interpreted".
/// </remarks>
internal sealed class MemberResult
{
    /// <summary>The member the invocation named.</summary>
    [JsonPropertyName("member")]
    public required string Member { get; init; }

    /// <summary>The transition the invocation asked for.</summary>
    [JsonPropertyName("transition")]
    public required string Transition { get; init; }

    /// <summary>What happened: <c>succeeded</c>, <c>refused</c>, or <c>uncertain</c>.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>The human-readable reason, always present for a refusal.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The member's state as the store holds it, or <c>null</c> when it was never hired.</summary>
    [JsonPropertyName("state")]
    public MemberStateResult? State { get; init; }

    /// <summary>The replacement handoff, when this invocation wrote one.</summary>
    [JsonPropertyName("handoff")]
    public MemberHandoffResult? Handoff { get; init; }

    /// <summary>Serializes the result to the JSON the host prints.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, RunResult.Json);

    /// <summary>Projects a stored member state onto the shape the host prints.</summary>
    public static MemberResult From(string member, string transition, MemberTransitionResult result) => new()
    {
        Member = member,
        Transition = transition,
        Outcome = result.Outcome switch
        {
            MemberTransitionOutcome.Succeeded => "succeeded",
            MemberTransitionOutcome.Uncertain => "uncertain",
            _ => "refused",
        },
        Message = result.Message,
        State = result.State is { } state ? MemberStateResult.From(state) : null,
    };
}

/// <summary>The durable member state, as the host prints it.</summary>
internal sealed class MemberStateResult
{
    /// <summary>The member's name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The lifecycle state as this build reads it.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    /// <summary>The state exactly as the store holds it, including a value this build cannot read.</summary>
    [JsonPropertyName("stateText")]
    public string? StateText { get; init; }

    /// <summary>The monotonic transition epoch.</summary>
    [JsonPropertyName("epoch")]
    public required long Epoch { get; init; }

    /// <summary>The instant the current state was committed.</summary>
    [JsonPropertyName("since")]
    public required DateTimeOffset Since { get; init; }

    /// <summary>The transition that produced the current state.</summary>
    [JsonPropertyName("lastTransition")]
    public string? LastTransition { get; init; }

    /// <summary>The reason recorded with the current state.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The task ids the member still holds.</summary>
    [JsonPropertyName("inFlight")]
    public required IReadOnlyList<string> InFlight { get; init; }

    /// <summary>The successor's name, when the member was replaced.</summary>
    [JsonPropertyName("replacedBy")]
    public string? ReplacedBy { get; init; }

    /// <summary>The handoff document's owner id, when the member was replaced.</summary>
    [JsonPropertyName("handoffOwnerId")]
    public string? HandoffOwnerId { get; init; }

    /// <summary>Projects a stored member state onto the shape the host prints.</summary>
    public static MemberStateResult From(DurableMemberState state) => new()
    {
        Name = state.Name,
        State = DurableMembers.Text(state.State),
        StateText = state.StateText,
        Epoch = state.Epoch,
        Since = state.Since,
        LastTransition = state.Transition,
        Reason = state.Reason,
        InFlight = state.InFlight,
        ReplacedBy = state.ReplacedBy,
        HandoffOwnerId = state.HandoffOwnerId,
    };
}

/// <summary>The replacement handoff, as the host prints it.</summary>
internal sealed class MemberHandoffResult
{
    /// <summary>The member being replaced.</summary>
    [JsonPropertyName("predecessor")]
    public required string Predecessor { get; init; }

    /// <summary>The member taking over.</summary>
    [JsonPropertyName("successor")]
    public required string Successor { get; init; }

    /// <summary>The task ids the predecessor still held when it was replaced.</summary>
    [JsonPropertyName("inFlight")]
    public required IReadOnlyList<string> InFlight { get; init; }

    /// <summary>The context handed to the successor.</summary>
    [JsonPropertyName("context")]
    public required string Context { get; init; }

    /// <summary>The instant the handoff was committed.</summary>
    [JsonPropertyName("writtenAt")]
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>Projects a stored handoff onto the shape the host prints.</summary>
    public static MemberHandoffResult From(DurableMemberHandoff handoff) => new()
    {
        Predecessor = handoff.Predecessor,
        Successor = handoff.Successor,
        InFlight = handoff.InFlight,
        Context = handoff.Context,
        WrittenAt = handoff.WrittenAt,
    };
}
