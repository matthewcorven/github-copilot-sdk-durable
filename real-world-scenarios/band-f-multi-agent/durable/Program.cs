using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Durable;
using GitHub.Copilot;

namespace BandF.Durable;

/// <summary>
/// The command line: <c>run</c>, <c>resume</c>, <c>wire</c> and <c>show</c>.
/// </summary>
/// <remarks>
/// <c>wire</c> and <c>show</c> exist so nothing in the evidence is read off stdout. They open the same
/// store through the same layer and print what it holds, which is the difference between "the program
/// said the delegation was committed" and "the delegation is in the store".
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "run";
        var rest = args.Skip(1).ToList();

        if (verb is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var runDirectory = Environment.GetEnvironmentVariable("BAND_F_RUN_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Directory.GetCurrentDirectory(), Names.StoreDirectory);
        var storePath = Path.Combine(runDirectory, "store.jsonl");

        return verb switch
        {
            "run" => await RunAsync(storePath, rest, fresh: true),
            "resume" => await RunAsync(storePath, rest, fresh: false),
            "wire" => ReadWire(storePath),
            "show" => Show(storePath),
            "verify" => Verify(storePath),
            _ => Unknown(verb),
        };
    }

    private const string Usage = """
        usage: BandFDurable <run|resume|wire|show> [options]

          run      create the run and serve its turns
          resume   reopen the run and give control back to the agent the checkpoint names
          wire     print the control-transfer log the store holds, one JSON object per line
          show     print the committed routing decisions and specialist outputs, per turn
          verify   check the store against the four durable claims, one line per claim

        options for run (recorded in the run's input; a resume ignores its own):
          --kill-after-routing <turn>[,<turn>...]     after that turn's routing decision commits,
                                                      announce a pid and wait to be killed
          --kill-after-delegating <turn>[,<turn>...]  the same, after the delegation commits
          --kill-in-turn <turn>[,<turn>...]           the same, inside a specialist's turn: the attempt
                                                      is committed and the model has not been asked

        environment:
          BAND_F_RUN_DIR   the run's directory (default ./.durable)
          BAND_F_ROUTING   model|scripted (default model)
          BAND_F_MODEL     the model every agent asks for (default: the harness's)
        """;

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"BandFDurable: unknown command '{verb}'. Use --help.");
        return 2;
    }

    // ── run / resume ────────────────────────────────────────────────────────

    private static async Task<int> RunAsync(string storePath, List<string> options, bool fresh)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

        var requestedRouting = (Environment.GetEnvironmentVariable("BAND_F_ROUTING") ?? "model").Trim().ToLowerInvariant()
            is "scripted" or "deterministic"
                ? "scripted"
                : "model";

        var model = Environment.GetEnvironmentVariable("BAND_F_MODEL") is { Length: > 0 } configuredModel
            ? configuredModel
            : DurableHarness.DefaultModel;

        using var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });

        var view = new RunView(store);
        if (!fresh && view.Task is null)
        {
            Console.Error.WriteLine($"resume: no run in {storePath}; start one with 'run'.");
            return 2;
        }

        if (fresh && view.Task is not null)
        {
            Console.Error.WriteLine($"run: {storePath} already holds a run; use 'resume', or a new directory.");
            return 2;
        }

        var log = new ConsoleLog();
        var registry = new ExtensionRegistry();

        // The tools are installed before the harness opens, because the harness builds a session from
        // the choices a conversation names, and those choices name extensions that must already exist.
        Router.Install(registry);
        var results = new SpecialistRunner.ToolResults();
        SpecialistRunner.Install(registry, results);

        await using var harness = await DurableHarness.OpenAsync(store, registry);

        // A resumed process reads the run's own input, so it cannot be handed a different script or a
        // different seam by its command line. The task definition is built from that, not from
        // `rest`.
        var input = fresh
            ? new DelegationInput
            {
                Turns = Script.Turns,
                Routing = requestedRouting,
                KillAfterRouting = TurnsFrom(options, "--kill-after-routing"),
                KillAfterDelegating = TurnsFrom(options, "--kill-after-delegating"),
                KillInTurn = TurnsFrom(options, "--kill-in-turn"),
            }
            : view.Input ?? new DelegationInput { Turns = Script.Turns, Routing = requestedRouting };

        var routingMode = (input.Routing == "scripted") ? RoutingMode.Scripted : RoutingMode.Model;
        var router = new Router(harness, routingMode, model);
        var specialist = new SpecialistRunner(harness, model, results, log);

        Console.WriteLine($"store:     {storePath}");
        Console.WriteLine($"routing:   {input.Routing} "
            + (routingMode == RoutingMode.Scripted
                ? "(the scenario decides; the coordinator makes no model call)"
                : "(the coordinator agent decides by calling the delegation tool)")
            + (fresh ? "" : " (from the run's stored input)"));
        Console.WriteLine($"model:     {model} "
            + (Environment.GetEnvironmentVariable("BAND_F_MODEL") is { Length: > 0 }
                ? "(BAND_F_MODEL)"
                : "(harness default)"));

        var task = new DelegationTask(harness, router, specialist, log, input);

        registry.Install(new Extension("band-f", tasks: [task.Definition()]));


        DurableTaskRecord record;
        if (fresh)
        {
            var coordinator = await harness.CreateConversationAsync(
                router.Choices(model),
                cancellationToken: CancellationToken.None);

            // Created without being scheduled, so the run's header prints before its first turn does.
            // `CreateTaskAsync` starts the task as it returns, which would interleave the first turn's
            // output with the lines that describe the run.
            record = await store.CommitAsync(tx => Task.FromResult(
                tx.CreateTask(task.Definition(), DurableJson.ToElement(input), ownerConversationId: coordinator.Id)));

            Console.WriteLine($"run:       new durable task {record.Id} in coordinator conversation {coordinator.Id} "
                + $"({input.Turns.Count} turn(s))");
            foreach (var killPoint in KillPoints(input))
            {
                Console.WriteLine($"kill:      {killPoint}");
            }
        }
        else
        {
            record = view.Task!;
            Console.WriteLine($"run:       resuming durable task {record.Id} "
                + $"(status {record.Status}, restarts before this process: {record.Restarts})");
            foreach (var killPoint in KillPoints(input))
            {
                Console.WriteLine($"kill:      {killPoint} (from the run's stored input)");
            }

            if (record.Status == global::Durable.TaskStatus.Terminal)
            {
                var finished = record.Outcome is { } stored
                    ? stored.Deserialize<global::Durable.TaskOutcome>(DurableJson.Options)!
                    : new global::Durable.TaskOutcome("completed");
                Console.WriteLine("resume:    the run is already finished; there is nothing to resume.");
                return Report(finished);
            }

            // The band's headline line, printed before the resumed run does anything: it reads who
            // holds the conversation out of the checkpoint.
            var checkpoint = view.Checkpoint;
            Console.WriteLine($"[control] resume: turn {checkpoint.Turn} phase '{checkpoint.Phase}' — "
                + $"{checkpoint.Holder} holds control (delegated at {checkpoint.DelegationEntry ?? "(none)"})");
            if (checkpoint.Holder == Agents.Coordinator)
            {
                Console.WriteLine("[control] the checkpoint names the coordinator, so this process re-enters routing; "
                    + "a routing decision already committed for this turn is reused, not re-made");
            }
            else
            {
                Console.WriteLine($"[control] the checkpoint names {checkpoint.Holder}, so control goes to "
                    + $"{checkpoint.Holder} — not back to {Agents.Coordinator}");
            }
        }

        Console.WriteLine(new string('─', 78));

        var outcome = await harness.RunTaskAsync(record.Id, CancellationToken.None);
        return Report(outcome);
    }

    private static IEnumerable<string> KillPoints(DelegationInput input)
    {
        foreach (var turn in input.KillAfterRouting)
        {
            yield return $"after turn {turn}'s routing decision commits";
        }

        foreach (var turn in input.KillAfterDelegating)
        {
            yield return $"after turn {turn}'s delegation commits";
        }

        foreach (var turn in input.KillInTurn)
        {
            yield return $"inside turn {turn}'s specialist turn, before the model is asked";
        }
    }

    private static int Report(global::Durable.TaskOutcome outcome)
    {
        var result = outcome.Result is { ValueKind: JsonValueKind.Object } element
            ? element.Deserialize<DelegationResult>(DurableJson.Options)
            : null;

        Console.WriteLine(new string('─', 78));
        if (outcome.IsCompleted)
        {
            Console.WriteLine($"result:    {result?.Reason ?? "completed"} after {result?.Turns ?? 0} turn(s)");
            if (result?.Holders is { Count: > 0 } holders)
            {
                Console.WriteLine("holders:   " + string.Join(" -> ", holders));
            }

            return 0;
        }

        Console.Error.WriteLine($"durable: the run did not complete: {outcome.Status} {outcome.Error}");
        return 1;
    }

    private static List<int> TurnsFrom(List<string> options, string name)
    {
        var index = options.IndexOf(name);
        if (index < 0 || index + 1 >= options.Count)
        {
            return [];
        }

        return options[index + 1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var turn) ? turn : -1)
            .Where(turn => turn >= 0)
            .ToList();
    }

    // ── wire / show ─────────────────────────────────────────────────────────

    /// <summary>Prints the control-transfer log the store holds, one JSON object per record.</summary>
    private static int ReadWire(string storePath) => WithRun(storePath, "wire", view =>
    {
        foreach (var (record, entryId, sequence) in view.Wire())
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                turn = record.Turn,
                @event = record.Event,
                agent = record.Agent,
                request = record.Request,
                detail = record.Detail,
                routingEntry = record.RoutingEntry,
                entry = entryId,
                seq = sequence,
            }, LogJson));
        }

        return 0;
    });

    /// <summary>Prints the routing decision and specialist output each turn's documents hold.</summary>
    private static int Show(string storePath) => WithRun(storePath, "show", view =>
    {
        foreach (var turn in view.Wire().Select(revision => revision.Record.Turn).Distinct().Order())
        {
            var user = view.Document(Docs.UserTurn, turn);
            var decision = view.Document(Docs.Routing, turn);
            var output = view.Document(Docs.Output, turn);

            Console.WriteLine($"turn {turn}");
            Console.WriteLine($"  user:     {user?.Message ?? "(none recorded)"}");
            Console.WriteLine($"  routing:  {(decision is null ? "(none)" : $"{decision.Specialist} for: {decision.Request}")}");
            Console.WriteLine($"            written {view.Versions(Docs.Routing, turn)} time(s), "
                + $"source {decision?.Source ?? "-"}{(decision?.Model is null ? string.Empty : $", model {decision.Model}")}");
            Console.WriteLine($"  output:   {(output is null ? "(none)" : output.Answer)}");
            if (output is not null)
            {
                Console.WriteLine($"            {output.Specialist}, {output.ModelTurns} model turn(s), "
                    + $"{output.ProducedBy}, tool {output.Tool} -> {output.ToolResult ?? "(not called)"}");
            }
        }

        return 0;
    });


    /// <summary>
    /// Checks the store against the claims this variant makes, reading only committed state.
    /// </summary>
    /// <remarks>
    /// The point of a separate verifier is that no claim is asserted about stdout. Each check is a
    /// question the store answers: how many times a document was written, whether two documents share
    /// an entry, whether the agent that produced a turn is the agent the routing decision named. A run
    /// that printed the right lines and committed the wrong state fails here.
    /// </remarks>
    private static int Verify(string storePath) => WithRun(storePath, "verify", view =>
    {
        var failures = 0;

        void Check(string name, bool held, string detail)
        {
            Console.WriteLine($"{(held ? "PASS" : "FAIL")}  {name}{(detail.Length == 0 ? string.Empty : $" — {detail}")}");
            if (!held)
            {
                failures++;
            }
        }

        var wire = view.Wire();
        var turns = wire.Select(revision => revision.Record.Turn).Distinct().Order().ToList();

        // 1. Each turn's routing decision was written exactly once. A second write is a re-route,
        //    which is the failure the memo exists to prevent.
        foreach (var turn in turns)
        {
            var writes = view.Versions(Docs.Routing, turn);
            Check($"turn {turn}: the routing decision was written once", writes == 1, $"written {writes} time(s)");
        }

        // 2. Every turn that was delegated has a specialist output, and the specialist that produced
        //    it is the one the routing decision named. A missing output is a failure, not a skip: the
        //    claim is that the store cannot hold a delegation without the output that discharges it,
        //    and a check that skips the absent case can never fail on it.
        foreach (var turn in turns)
        {
            var decision = view.Document(Docs.Routing, turn);
            var output = view.Document(Docs.Output, turn);

            if (output is null)
            {
                Check($"turn {turn}: no output for a committed delegation", false,
                    "the store holds no band-f.specialist-output value for this turn");
                continue;
            }

            Check($"turn {turn}: {output.Specialist} produced the turn it was routed",
                decision is not null && output.Specialist == decision.Specialist,
                decision is null
                    ? $"produced by {output.Specialist}, but no routing decision is stored"
                    : $"routed {decision.Specialist}, produced by {output.Specialist}");
        }

        // 3. Every delegation names the routing decision it was made from, and that decision is in
        //    the store. A delegation made from a fresh answer instead of the memo has no such entry
        //    to name.
        foreach (var turn in turns)
        {
            var delegation = wire.LastOrDefault(revision => revision.Record.Turn == turn && revision.Record.Event.StartsWith("delegated", StringComparison.Ordinal));
            var routing = view.Store.Document(Docs.Routing.Kind, RunView.OwnerFor(turn))?.Versions.LastOrDefault();
            Check($"turn {turn}: the delegation names the routing decision it was made from",
                delegation.Record?.RoutingEntry is { Length: > 0 } named && routing is not null && named == routing.EntryId,
                $"delegation names {delegation.Record?.RoutingEntry ?? "(none)"}, store holds {routing?.EntryId ?? "(none)"}");
        }

        // 3b. Control moved once per turn. The record is "delegated" when no decision existed yet and
        //     "delegated-from-memo" when this process read one; a second delegation for the same turn
        //     is a memo miss, and that is what the control in evidence.sh stage 3c produces.
        foreach (var turn in turns)
        {
            var delegations = wire.Where(revision => revision.Record.Turn == turn
                && revision.Record.Event.StartsWith("delegated", StringComparison.Ordinal)).ToList();
            var reRouted = delegations.Count(revision => revision.Record.Event == "delegated") > 1
                || delegations.Count > 1;
            Check($"turn {turn}: control moved once from the committed decision",
                !reRouted,
                $"{delegations.Count} delegation record(s): "
                + string.Join(", ", delegations.Select(revision => revision.Record.Event)));
        }

        // 4. The specialist's output and the fact that it was produced share an entry. A missing
        //    `produced` record is a failure for the same reason as a missing output: the record is
        //    what says the output exists, and its absence is the half-written state this claim rules
        //    out.
        foreach (var turn in turns)
        {
            var produced = wire.LastOrDefault(revision => revision.Record.Turn == turn && revision.Record.Event == "produced");
            var output = view.Store.Document(Docs.Output.Kind, RunView.OwnerFor(turn))?.Versions.LastOrDefault();

            if (produced.Record is null)
            {
                Check($"turn {turn}: no output for a committed delegation", false,
                    "the store holds no produced record for this turn");
                continue;
            }

            Check($"turn {turn}: the output shares its entry with the record that says it exists",
                output is not null && produced.EntryId == output.EntryId,
                $"produced {produced.EntryId}, output {output?.EntryId ?? "(none)"}");
        }

        // 4b. The run's closing `holders` list names every delegation the store holds. A filter on
        //     the bare event name would drop every turn a resume served, because those record
        //     "delegated-from-memo" — a bug that is invisible in the store and visible only in the
        //     summary, which is exactly the sort of thing a claim has to catch.
        // The task's outcome is the layer's TaskOutcome wrapper; the run's own result is its
        // `Result`, and a store whose outcome is not shaped like that is a failure to report rather
        // than an exception to throw out of a verifier.
        var task = view.Task!;
        IReadOnlyList<string> reported = [];
        if (task.Outcome is { } outcome && outcome.ValueKind == JsonValueKind.Object)
        {
            var wrapped = outcome.Deserialize<global::Durable.TaskOutcome>(DurableJson.Options);
            if (wrapped?.Result is { ValueKind: JsonValueKind.Object } result)
            {
                reported = result.Deserialize<DelegationResult>(DurableJson.Options)?.Holders ?? [];
            }
        }
        var delegated = wire
            .Where(revision => revision.Record.Event.StartsWith("delegated", StringComparison.Ordinal))
            .Select(revision => revision.Record.Agent)
            .ToList();
        Check("the reported holders name every delegation in the store",
            reported.SequenceEqual(delegated),
            reported.Count == 0 && delegated.Count == 0
                ? "the run has not finished and reports no holders"
                : $"reported [{string.Join(", ", reported)}], store [{string.Join(", ", delegated)}]");

        // 5. Every turn in the run's script reached the store.
        var scriptTurns = view.Input?.Turns.Count ?? 0;
        Check($"the run served all {scriptTurns} turn(s) in its script", turns.Count == scriptTurns,
            $"store holds {turns.Count} turn(s)");

        Console.WriteLine(failures == 0
            ? "verify: every claim held"
            : $"verify: {failures} claim(s) did not hold");
        return failures == 0 ? 0 : 1;
    });

    private static int WithRun(string storePath, string verb, Func<RunView, int> body)
    {
        if (!File.Exists(storePath))
        {
            Console.Error.WriteLine($"{verb}: no store at {storePath}");
            return 2;
        }

        using var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });
        var view = new RunView(store);
        if (view.Task is null)
        {
            Console.Error.WriteLine($"{verb}: no run in {storePath}");
            return 2;
        }

        return body(view);
    }

    /// <summary>Compact JSON, so a wire line is one diffable line.</summary>
    internal static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
