using System.Text.Json;
using System.Text.Json.Serialization;

namespace Durable.Runtime;

/// <summary>
/// The result of one <c>schedule</c> invocation: the schedule as the store now holds it, and
/// the occurrences this invocation ran.
/// </summary>
/// <remarks>
/// This is a separate shape from <see cref="RunResult"/> on purpose. A run result describes one
/// occurrence and is shared with every transport; a schedule result describes the schedule and
/// nests the occurrences inside it, so a caller reads one JSON document rather than a stream of
/// them. <see cref="Schedule"/> is <c>null</c> when the store holds no schedule for the agent,
/// which is how a caller tells "nothing is scheduled" from "the schedule is disabled".
/// </remarks>
internal sealed class ScheduleResult
{
    /// <summary>The agent the schedule belongs to.</summary>
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    /// <summary>The schedule as the store now holds it, or <c>null</c> when there is none.</summary>
    [JsonPropertyName("schedule")]
    public ScheduleStateResult? Schedule { get; init; }

    /// <summary>The occurrences this invocation ran, in the order they ran.</summary>
    [JsonPropertyName("occurrences")]
    public required IReadOnlyList<RunResult> Occurrences { get; init; }

    /// <summary>Serializes the result to the JSON the host prints.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, RunResult.Json);
}

/// <summary>The durable state of one schedule, as the store holds it.</summary>
internal sealed class ScheduleStateResult
{
    /// <summary>The interval as it was written, e.g. <c>5m</c>.</summary>
    [JsonPropertyName("every")]
    public required string Every { get; init; }

    /// <summary>The missed-run policy by name: <c>skip</c>, <c>coalesce</c>, or <c>run-once</c>.</summary>
    [JsonPropertyName("missedRun")]
    public required string MissedRun { get; init; }

    /// <summary>The next instant the schedule is due, in UTC.</summary>
    [JsonPropertyName("nextDue")]
    public required DateTimeOffset NextDue { get; init; }

    /// <summary>The instant of the most recent occurrence, or <c>null</c> when none has run.</summary>
    [JsonPropertyName("lastRun")]
    public DateTimeOffset? LastRun { get; init; }

    /// <summary>How many occurrences the schedule has run.</summary>
    [JsonPropertyName("runs")]
    public required long Runs { get; init; }

    /// <summary>Whether the scheduler drives this schedule.</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    /// <summary>Projects a stored schedule onto the shape the host prints.</summary>
    public static ScheduleStateResult From(DurableSchedule schedule) => new()
    {
        Every = schedule.Every,
        MissedRun = MissedRunPolicies.Text(schedule.MissedRun),
        NextDue = schedule.NextDue,
        LastRun = schedule.LastRun,
        Runs = schedule.Runs,
        Enabled = schedule.Enabled,
    };
}
