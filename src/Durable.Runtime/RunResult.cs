using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Durable.Runtime;

/// <summary>
/// The result of one agent occurrence: the single shape every transport serializes.
/// </summary>
/// <remarks>
/// The shape is deliberately flat and complete. A caller that only reads the exit code still
/// gets the identifiers it needs to look the occurrence up again, and a caller that reads the
/// JSON never has to reconstruct a fact the host already knew.
/// <para>
/// <see cref="EffectiveModel"/> is the runtime's own report, never the model the definition
/// requested. When the runtime reported nothing it is <c>null</c> and
/// <see cref="ModelReported"/> is <c>false</c>; there is no fallback to the requested model or
/// to <see cref="DurableHarness.DefaultModel"/>.
/// </para>
/// </remarks>
internal sealed class RunResult
{
    /// <summary>The JSON the host writes to standard output.</summary>
    /// <remarks>
    /// Nulls are written rather than omitted: a consumer that reads <c>effectiveModel</c> has to
    /// be able to tell "the runtime reported no model" from "this build does not report models",
    /// and an absent property cannot say which.
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>The agent definition that ran.</summary>
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    /// <summary>The conversation the occurrence belongs to. Stable across occurrences.</summary>
    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    /// <summary>The admission this occurrence wrote, or <c>null</c> when nothing was admitted.</summary>
    [JsonPropertyName("admissionId")]
    public string? AdmissionId { get; init; }

    /// <summary>The last checkpoint this occurrence produced, or <c>null</c> when it produced none.</summary>
    [JsonPropertyName("checkpointId")]
    public string? CheckpointId { get; init; }

    /// <summary>The model the runtime reported, or <c>null</c> when it reported none.</summary>
    [JsonPropertyName("effectiveModel")]
    public EffectiveModelResult? EffectiveModel { get; init; }

    /// <summary>Whether the runtime reported a model at all.</summary>
    [JsonPropertyName("modelReported")]
    public required bool ModelReported { get; init; }

    /// <summary>How much of the agent's authority the host can enforce.</summary>
    [JsonPropertyName("containment")]
    public required string Containment { get; init; }

    /// <summary>Why the containment was classified that way.</summary>
    [JsonPropertyName("containmentReason")]
    public required string ContainmentReason { get; init; }

    /// <summary>What happened: <c>succeeded</c>, <c>turnUnsuccessful</c>, or <c>agentFailed</c>.</summary>
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    /// <summary>The failure that stopped the occurrence, or <c>null</c> when nothing failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Serializes the result to the JSON the host prints.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>The model the runtime reported, and the event that carried it.</summary>
internal sealed class EffectiveModelResult
{
    /// <summary>The model identifier the runtime reported.</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Where the report came from: <c>session</c>, <c>turn</c>, or <c>usage</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}
