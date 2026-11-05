using System.Globalization;
using System.Text.Json;
using Durable;

namespace BandC.Durable;

/// <summary>
/// The process: open the store, decide whether this is a new run or a resume, and drive the task.
/// </summary>
/// <remarks>
/// This file is durability — it is the whole host. Everything it does exists because the state is in
/// a store: the exclusive open is what refuses an overlap, and the resume branch is what a run record
/// buys.
/// </remarks>
public static class Runner
{
    /// <summary>Runs one process and returns its exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"durable: {ex.Message}");
            Console.Error.WriteLine(
                "usage: BandCDurable --store <file> --run-dir <dir> [--fixture <file>] "
                + "[--now <iso8601>] [--reset]");
            return 2;
        }

        ExitSeam? seam;
        try
        {
            seam = ExitSeam.FromEnvironment();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"durable: {ex.Message}");
            return 2;
        }

        DurableStore store;
        try
        {
            // The open is where an overlap is refused. A persistent store takes an exclusive lock on
            // its file for the store's lifetime, so this throws rather than interleaving two writers
            // — see README.md §"Concurrency" for the message and for what it costs.
            store = DurableStore.Create(new DurableStoreOptions
            {
                Path = options.StorePath,
                Format = DurableStoreFormat.Jsonl,
            });
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"durable: cannot open the store: {ex.Message}");
            Console.Error.WriteLine(
                "durable: a second run against the same repository is refused rather than serialized; "
                + "see real-world-scenarios/band-c-ci-repo-agent/durable/README.md §Concurrency.");
            return 3;
        }

        try
        {
            return await DriveAsync(store, options, seam);
        }
        finally
        {
            store.Dispose();
        }
    }

    private static async Task<int> DriveAsync(DurableStore store, Options options, ExitSeam? seam)
    {
        var fixture = Fixture.Load(options.FixturePath);
        Directory.CreateDirectory(options.RunDirectory);
        var runId = TriageStore.RunId(fixture.Repository);

        Console.WriteLine("band C durable — the pr-triage agent with its state in a Durable store");
        Console.WriteLine($"run time:   {options.BaseTime}");
        Console.WriteLine($"repository: {fixture.Repository}");
        Console.WriteLine($"store:      {options.StorePath}");
        Console.WriteLine($"fixture:    {options.FixturePath} ({fixture.PullRequests.Count} pull requests)");
        Console.WriteLine($"output:     {options.RunDirectory}");
        if (seam is not null)
        {
            var description = new List<string>();
            if (seam.Phase is not null)
            {
                description.Add($"BAND_C_EXIT_AFTER_PHASE={seam.Phase}");
            }

            if (seam.AfterComments is { } limit)
            {
                description.Add($"BAND_C_EXIT_AFTER_COMMENTS={limit}");
            }

            if (seam.AfterCommentEffect)
            {
                description.Add("BAND_C_EXIT_AFTER_COMMENT_EFFECT=1");
            }

            if (seam.GuardDisabled)
            {
                description.Add("BAND_C_NO_IDEMPOTENCY_GUARD=1");
            }

            if (seam.LedgerHidden)
            {
                description.Add("BAND_C_HIDE_LEDGER=1");
            }

            Console.WriteLine($"seam:       {string.Join(" ", description)} (exit {ExitSeam.ExitCode})");
        }

        Console.WriteLine();

        var registry = new ExtensionRegistry();
        var definition = TriagePhases.Build(store, seam);
        registry.Install(new Extension(Store.ExtensionName, tasks: [definition]));

        // No model, no session: a task is a durable state machine, not a conversation, and this
        // scenario's phases are deterministic functions of the fixture. The harness is opened for its
        // task runner and its store, not for a conversation — README.md §"Running without a model"
        // records that this works and why it is the right shape here.
        await using var harness = await DurableHarness.OpenAsync(store, registry, CancellationToken.None);

        var existing = TriageStore.CurrentTask(store);
        if (existing is not null && existing.Status == global::Durable.TaskStatus.Terminal && !options.Reset)
        {
            Console.WriteLine($"run:        task {existing.Id} is finished (generation "
                + $"{(TriageStore.Run(store, runId)?.Generation ?? 0)}); starting a new one");
            existing = null;
        }

        string taskId;
        if (existing is null)
        {
            var journal = await TriageStore.CreateJournalAsync(store);
            var record = await harness.CreateTaskAsync(
                definition,
                DurableJson.ToElement(new RunInput
                {
                    FixturePath = options.FixturePath,
                    OutputDirectory = options.RunDirectory,
                    BaseTime = options.BaseTime,
                    Journal = journal,
                }),
                cancellationToken: CancellationToken.None);
            taskId = record.Id;
            Console.WriteLine($"run:        new durable task {record.Id} (journal {journal})");
            Console.WriteLine(new string('─', 72));
        }
        else
        {
            var state = JsonSerializer.Deserialize<RunState>(existing.Checkpoint.GetRawText(), DurableJson.Options) ?? new RunState();
            Console.WriteLine($"run:        resuming durable task {existing.Id} at phase '{state.Phase}' "
                + $"(attempt {state.Attempts + 1}, restarts so far {existing.Restarts})");
            Console.WriteLine($"resuming:   {state.Commented.Count} comment(s) recorded in this run's record");
            Console.WriteLine($"memos:      {existing.Memos.Count} step memo(s) on record");
            Console.WriteLine(new string('─', 72));
            taskId = existing.Id;
        }

        TaskOutcome outcome;
        try
        {
            outcome = await harness.WaitForTaskAsync(taskId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("run:        stopped; the store holds the checkpoint.");
            return 0;
        }

        Console.WriteLine();
        if (outcome.IsCompleted)
        {
            var result = outcome.Result is { ValueKind: System.Text.Json.JsonValueKind.Object } value
                ? JsonSerializer.Deserialize<RunResult>(value.GetRawText(), DurableJson.Options)
                : null;
            Console.WriteLine($"run:        completed — {result?.Triaged ?? 0} triaged, "
                + $"{result?.Commented ?? 0} commented, {result?.InCooldown ?? 0} in cooldown, "
                + $"{result?.NotAgent ?? 0} not ours, {result?.NotOpen ?? 0} not open");
            return 0;
        }

        Console.Error.WriteLine($"durable: run did not complete: {outcome.Status} {outcome.Error}");
        return 1;
    }
}

/// <summary>The command line.</summary>
public sealed class Options
{
    public required string StorePath { get; init; }
    public required string RunDirectory { get; init; }
    public required string FixturePath { get; init; }
    public required string BaseTime { get; init; }

    /// <summary>Whether to start a new generation even though the last task finished.</summary>
    public bool Reset { get; init; }

    public static Options Parse(string[] args)
    {
        string? store = null, runDirectory = null, fixture = null, now = null;
        var reset = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--store":
                    store = Next(args, ref i);
                    break;
                case "--run-dir":
                    runDirectory = Next(args, ref i);
                    break;
                case "--fixture":
                    fixture = Next(args, ref i);
                    break;
                case "--now":
                    now = Next(args, ref i);
                    break;
                case "--reset":
                    reset = true;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (store is null)
        {
            throw new ArgumentException("--store is required");
        }

        if (runDirectory is null)
        {
            throw new ArgumentException("--run-dir is required");
        }

        var fixturePath = fixture is null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixture", "pull-requests.json"))
            : Path.GetFullPath(fixture);
        var loaded = Fixture.Load(fixturePath);
        var baseTime = now is null
            ? DateTimeOffset.Parse(loaded.CapturedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : DateTimeOffset.Parse(now, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        return new Options
        {
            StorePath = Path.GetFullPath(store),
            RunDirectory = Path.GetFullPath(runDirectory),
            FixturePath = fixturePath,
            BaseTime = Triage.Iso(baseTime),
            Reset = reset,
        };
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[i]} needs a value");
        }

        return args[++i];
    }
}
