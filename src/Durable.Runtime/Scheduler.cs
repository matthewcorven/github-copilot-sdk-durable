namespace Durable.Runtime;

/// <summary>Reads and writes the durable schedule records.</summary>
/// <remarks>
/// A schedule is a document, not a timer. The store is the only place a schedule lives, so a
/// host that restarts reads the same schedule back and a host that never wrote one drives
/// nothing. The owner id is <c>schedule:{agent}</c>, which keeps the schedule out of every
/// conversation and out of every fork.
/// </remarks>
internal static class ScheduleStore
{
    /// <summary>The definition a schedule is stored under.</summary>
    public static readonly DocumentDefinition<DurableSchedule> Definition =
        new(DurableSchedule.DocumentKind, DocumentHistory.Rewindable, ForkPolicy.Initial);

    /// <summary>Every schedule in the store, enabled or not, ordered by agent.</summary>
    public static IReadOnlyList<DurableSchedule> Read(DurableStore store) =>
        store.Documents
            .Where(document => document.Kind == DurableSchedule.DocumentKind)
            .Select(document => document.Latest is { } latest
                ? DurableJson.FromElement<DurableSchedule>(latest)
                : null)
            .OfType<DurableSchedule>()
            .OrderBy(schedule => schedule.Agent, StringComparer.Ordinal)
            .ToList();

    /// <summary>The schedules the scheduler drives: the ones whose latest version is enabled.</summary>
    public static IReadOnlyList<DurableSchedule> Enabled(DurableStore store) =>
        Read(store).Where(schedule => schedule.Enabled).ToList();

    /// <summary>The schedule for an agent, or <see langword="null"/> if the store has none.</summary>
    public static DurableSchedule? Find(DurableStore store, string agent) =>
        Read(store).FirstOrDefault(schedule => string.Equals(schedule.Agent, agent, StringComparison.Ordinal));

    /// <summary>Commits a new version of a schedule.</summary>
    public static Task SaveAsync(DurableStore store, DurableSchedule schedule)
    {
        var ownerId = DurableSchedule.OwnerId(schedule.Agent);
        return store.CommitAsync(async transaction =>
        {
            var draft = transaction.Doc(Definition, ownerId);
            draft.Value = schedule;
            draft.Save(transaction.AppendEntry(ownerId, "schedule"));
            await Task.CompletedTask;
        });
    }
}

/// <summary>The command-line spelling of the missed-run policies.</summary>
/// <remarks>
/// The names are hyphenated on the command line and camel-cased in the store, because a person
/// types <c>run-once</c> and a JSON reader sees <c>runOnce</c>. Both spellings are accepted on
/// the command line so a script that copied a stored value still works.
/// </remarks>
internal static class MissedRunPolicies
{
    /// <summary>The names a caller may pass to <c>--missed-run</c>.</summary>
    public const string Names = "skip, coalesce, and run-once";

    /// <summary>Parses a policy name, or throws a usage error naming the accepted values.</summary>
    public static MissedRunPolicy Parse(string? text) => text switch
    {
        null or "" or "skip" => MissedRunPolicy.Skip,
        "coalesce" => MissedRunPolicy.Coalesce,
        "run-once" or "runonce" => MissedRunPolicy.RunOnce,
        _ => throw new UsageException($"unknown missed-run policy '{text}'. Policies are {Names}."),
    };

    /// <summary>The command-line spelling of a policy.</summary>
    public static string Text(MissedRunPolicy policy) => policy switch
    {
        MissedRunPolicy.Coalesce => "coalesce",
        MissedRunPolicy.RunOnce => "run-once",
        _ => "skip",
    };
}

/// <summary>What one scheduler pass decided to do with one schedule.</summary>
/// <param name="Before">The schedule as the store held it.</param>
/// <param name="After">The schedule to commit, which is <paramref name="Before"/> when nothing changed.</param>
/// <param name="Run">Whether this pass runs an occurrence.</param>
/// <param name="Occurrence">The scheduled instant the occurrence stands for, when <paramref name="Run"/> is true.</param>
internal sealed record SchedulePass(
    DurableSchedule Before,
    DurableSchedule After,
    bool Run,
    DateTimeOffset? Occurrence);

/// <summary>The missed-run policy, applied to one schedule at one instant.</summary>
/// <remarks>
/// The three policies differ in exactly two observable ways: whether an occurrence runs, and
/// where <see cref="DurableSchedule.NextDue"/> lands afterwards. <c>skip</c> runs nothing and
/// jumps the cadence past now. <c>coalesce</c> runs once and jumps the cadence past now.
/// <c>run-once</c> runs once and advances the cadence by a single interval, so the backlog is
/// still there for the next pass.
/// </remarks>
internal static class SchedulePolicy
{
    /// <summary>Decides what a pass at <paramref name="now"/> does with <paramref name="schedule"/>.</summary>
    public static SchedulePass Apply(DurableSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || schedule.NextDue > now)
        {
            return new SchedulePass(schedule, schedule, Run: false, Occurrence: null);
        }

        var interval = schedule.Interval;
        if (interval <= TimeSpan.Zero)
        {
            // A schedule with a non-positive interval cannot advance, so it is treated as
            // disabled rather than as a schedule that runs on every pass forever.
            return new SchedulePass(schedule, schedule with { Enabled = false }, Run: false, Occurrence: null);
        }

        // How many instants at or before now the schedule has not accounted for. Computed
        // arithmetically so a long outage costs the same as a short one.
        var missed = (now - schedule.NextDue).Ticks / interval.Ticks + 1;
        var afterNow = schedule.NextDue + TimeSpan.FromTicks(interval.Ticks * missed);

        return schedule.MissedRun switch
        {
            MissedRunPolicy.Skip => new SchedulePass(
                schedule,
                schedule with { NextDue = afterNow },
                Run: false,
                Occurrence: null),

            MissedRunPolicy.Coalesce => new SchedulePass(
                schedule,
                schedule with { NextDue = afterNow, LastRun = now, Runs = schedule.Runs + 1 },
                Run: true,
                Occurrence: now),

            _ => new SchedulePass(
                schedule,
                schedule with
                {
                    NextDue = schedule.NextDue + interval,
                    LastRun = schedule.NextDue,
                    Runs = schedule.Runs + 1,
                },
                Run: true,
                Occurrence: schedule.NextDue),
        };
    }
}

/// <summary>Drives the persisted schedules on a wall clock.</summary>
/// <remarks>
/// The scheduler holds no schedule of its own. Every pass re-reads the store, so a schedule
/// added, changed, or removed by another process is picked up on the next pass, and a host that
/// restarts resumes from the durable <see cref="DurableSchedule.NextDue"/> rather than from a
/// timer that died with the previous process.
/// <para>
/// An occurrence is claimed durably before it runs: the pass commits the advanced schedule and
/// only then runs the turn. A host that dies mid-occurrence therefore loses that occurrence
/// rather than repeating it, which is what "one occurrence per scheduled instant" means here.
/// </para>
/// </remarks>
internal sealed class Scheduler
{
    /// <summary>The longest the loop sleeps before re-reading the store.</summary>
    private static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(30);

    /// <summary>The shortest the loop sleeps, so a due schedule cannot spin the process.</summary>
    private static readonly TimeSpan MinIdle = TimeSpan.FromMilliseconds(100);

    private readonly DurableStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<DurableSchedule, DateTimeOffset, Task<RunResult>> _run;
    private readonly string? _agent;

    /// <summary>Creates a scheduler over a store, a clock, and a way to run one occurrence.</summary>
    /// <param name="store">The store the schedules live in.</param>
    /// <param name="clock">The wall clock, injected so a test can stand at a chosen instant.</param>
    /// <param name="run">Runs one occurrence of a schedule at a scheduled instant.</param>
    /// <param name="agent">
    /// The agent to restrict the loop to, or <see langword="null"/> to drive every enabled
    /// schedule in the store.
    /// </param>
    public Scheduler(
        DurableStore store,
        Func<DateTimeOffset> clock,
        Func<DurableSchedule, DateTimeOffset, Task<RunResult>> run,
        string? agent = null)
    {
        _store = store;
        _clock = clock;
        _run = run;
        _agent = agent;
    }

    /// <summary>The schedules this scheduler drives, in agent order.</summary>
    private IReadOnlyList<DurableSchedule> Due() =>
        ScheduleStore.Enabled(_store)
            .Where(schedule => _agent is null || string.Equals(schedule.Agent, _agent, StringComparison.Ordinal))
            .ToList();

    /// <summary>Runs one pass over every enabled schedule and returns the occurrences it ran.</summary>
    public async Task<IReadOnlyList<RunResult>> PassAsync(CancellationToken cancellationToken)
    {
        var results = new List<RunResult>();

        foreach (var schedule in Due())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pass = SchedulePolicy.Apply(schedule, _clock());
            if (pass.After != pass.Before)
            {
                await ScheduleStore.SaveAsync(_store, pass.After);
            }

            if (!pass.Run)
            {
                continue;
            }

            results.Add(await _run(pass.After, pass.Occurrence!.Value));
        }

        return results;
    }

    /// <summary>
    /// Waits until the earliest durable <see cref="DurableSchedule.NextDue"/>, clamped so the
    /// loop re-reads the store at least every <see cref="MaxIdle"/> and never spins.
    /// </summary>
    public async Task DelayUntilNextDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        var next = Due()
            .Select(schedule => schedule.NextDue)
            .DefaultIfEmpty(now + MaxIdle)
            .Min();

        var delay = next - now;
        if (delay < MinIdle)
        {
            delay = MinIdle;
        }
        else if (delay > MaxIdle)
        {
            delay = MaxIdle;
        }

        await Task.Delay(delay, cancellationToken);
    }
}
