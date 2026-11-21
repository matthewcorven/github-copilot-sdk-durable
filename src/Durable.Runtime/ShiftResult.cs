using System.Text.Json;
using System.Text.Json.Serialization;
using Durable;

namespace Durable.Runtime;

/// <summary>
/// The result of one <c>shift</c> invocation: the claim as the store now holds it, the handoff
/// this invocation committed, and the occurrences it ran.
/// </summary>
/// <remarks>
/// <see cref="Claim"/> is always present, including on a refusal, because the refusal's whole
/// value is naming the owner that holds the shift. <see cref="Outcome"/> is the vocabulary the
/// exit code maps from, so a caller that ignores the JSON still sees the difference between
/// "someone else holds it", "the state is uncertain", and "this shift ran".
/// </remarks>
internal sealed class ShiftResult
{
    /// <summary>The agent the shift belongs to.</summary>
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    /// <summary>The shift key the claim was written under.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>
    /// What happened: <c>ran</c>, <c>conflict</c>, <c>readOnly</c>, or <c>handoffRefused</c>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>The reason the invocation produced this outcome.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The claim as the store holds it, or <c>null</c> when it holds none.</summary>
    [JsonPropertyName("claim")]
    public ShiftClaimStateResult? Claim { get; init; }

    /// <summary>The handoff this invocation committed, or <c>null</c> when it wrote none.</summary>
    [JsonPropertyName("handoff")]
    public ShiftHandoffResult? Handoff { get; init; }

    /// <summary>The handoff that was read back before running, or <c>null</c> when resuming fresh.</summary>
    [JsonPropertyName("resumedFrom")]
    public ShiftHandoffResult? ResumedFrom { get; init; }

    /// <summary>The occurrences this invocation ran, in the order they ran.</summary>
    [JsonPropertyName("occurrences")]
    public required IReadOnlyList<RunResult> Occurrences { get; init; }

    /// <summary>Serializes the result to the JSON the host prints.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, RunResult.Json);
}

/// <summary>The durable state of one shift claim, as the store holds it.</summary>
internal sealed class ShiftClaimStateResult
{
    /// <summary>The owner token the shift was granted to.</summary>
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    /// <summary>The claim state as this build reads it: <c>active</c>, <c>released</c>, or <c>uncertain</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>The status exactly as the store holds it, including a value this build cannot read.</summary>
    [JsonPropertyName("statusText")]
    public string? StatusText { get; init; }

    /// <summary>The acquisition counter.</summary>
    [JsonPropertyName("attempt")]
    public required long Attempt { get; init; }

    /// <summary>The attempt this grant resumed from, or <c>null</c> for a fresh shift.</summary>
    [JsonPropertyName("resumedFrom")]
    public long? ResumedFrom { get; init; }

    /// <summary>When the grant was committed.</summary>
    [JsonPropertyName("claimedAt")]
    public required DateTimeOffset ClaimedAt { get; init; }

    /// <summary>When the owner released it, or <c>null</c> while held.</summary>
    [JsonPropertyName("releasedAt")]
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>Projects a stored claim onto the shape the host prints.</summary>
    public static ShiftClaimStateResult From(DurableShiftClaim claim) => new()
    {
        Owner = claim.Owner,
        Status = DurableShifts.Text(claim.Status),
        StatusText = claim.StatusText,
        Attempt = claim.Attempt,
        ResumedFrom = claim.ResumedFrom,
        ClaimedAt = claim.ClaimedAt,
        ReleasedAt = claim.ReleasedAt,
    };
}

/// <summary>The durable state of one shift handoff, as the store holds it.</summary>
internal sealed class ShiftHandoffResult
{
    /// <summary>The attempt the handoff describes.</summary>
    [JsonPropertyName("attempt")]
    public required long Attempt { get; init; }

    /// <summary>The owner that wrote it.</summary>
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    /// <summary>Whether the handoff records work starting (<c>running</c>) or stopping (<c>handedOff</c>).</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>What the shift worked on.</summary>
    [JsonPropertyName("selected")]
    public string? Selected { get; init; }

    /// <summary>What it completed.</summary>
    [JsonPropertyName("achieved")]
    public string? Achieved { get; init; }

    /// <summary>What is still owed.</summary>
    [JsonPropertyName("remaining")]
    public string? Remaining { get; init; }

    /// <summary>The next action a successor takes first.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }

    /// <summary>Blockers a successor must know about.</summary>
    [JsonPropertyName("blockers")]
    public string? Blockers { get; init; }

    /// <summary>When it was committed.</summary>
    [JsonPropertyName("writtenAt")]
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>Projects a stored handoff onto the shape the host prints.</summary>
    public static ShiftHandoffResult From(DurableShiftHandoff handoff) => new()
    {
        Attempt = handoff.Attempt,
        Owner = handoff.Owner,
        Phase = handoff.Phase is ShiftHandoffPhase.Running ? "running" : "handedOff",
        Selected = handoff.Selected,
        Achieved = handoff.Achieved,
        Remaining = handoff.Remaining,
        Next = handoff.Next,
        Blockers = handoff.Blockers,
        WrittenAt = handoff.WrittenAt,
    };
}
