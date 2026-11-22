using AgentOrchestrator.Durable;
using Durable;
using System.Text.Json;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The command-line driver: the same durable code path the browser uses, without a browser.
/// </summary>
/// <remarks>
/// A Blazor page cannot be driven from a shell script, and the restart and concurrency evidence
/// has to be reproducible. This driver creates enquiries through <see cref="AppState"/> — the same
/// object the page injects — and prints the committed transcript, so the evidence is the
/// application's own behaviour rather than a second implementation of it.
/// </remarks>
public static class Headless
{
    /// <summary>Runs the driver when the arguments ask for it.</summary>
    /// <returns><c>true</c> when the driver handled the arguments and the web host must not start.</returns>
    public static bool TryRun(string[] args, string storePath, PropertyDatabase database)
    {
        var enquiries = new List<string>();
        string? exitAfter = null;
        var resume = false;
        var contend = 0;
        var lateWakeup = false;
        var toolPath = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--enquiry" when i + 1 < args.Length:
                    enquiries.Add(args[++i]);
                    break;
                case "--exit-after-phase" when i + 1 < args.Length:
                    exitAfter = args[++i];
                    Environment.SetEnvironmentVariable(ExitSeam.PhaseVariable, exitAfter);
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--contend" when i + 1 < args.Length:
                    contend = int.Parse(args[++i]);
                    break;
                case "--late-wakeup":
                    lateWakeup = true;
                    break;
                case "--tool-path":
                    toolPath = true;
                    break;
                default:
                    return false;
            }
        }

        if (enquiries.Count == 0 && !resume)
        {
            return false;
        }

        if (contend > 0 && enquiries.Count == 0)
        {
            return false;
        }

        Environment.SetEnvironmentVariable("BAND_A_STORE", storePath);

        var harnessOptions = new DurableHarnessOptions();
        if (Environment.GetEnvironmentVariable("BAND_A_FIXTURE") is { Length: > 0 } fixture)
        {
            harnessOptions.RuntimeArguments = ["--fixture", fixture];
        }

        var store = EnquiryStore.OpenAsync(storePath, database, harnessOptions).GetAwaiter().GetResult();
        var app = new AppState(store);

        Console.WriteLine($"store:          {storePath}");
        Console.WriteLine($"store persists: {store.Store.IsPersistent}");
        Console.WriteLine($"conversations:  {store.Store.Conversations.Count}");
        Console.WriteLine($"tasks:          {store.Store.Tasks.Count}");

        if (resume)
        {
            var resumed = app.ResumeUnfinished();
            Console.WriteLine($"resumed tasks:  {resumed.Count}");
            foreach (var taskId in resumed)
            {
                var record = store.Store.Task(taskId)!;
                var state = EnquiryDurable.Read(store.Store, record.OwnerConversationId!);
                // The document, not the task checkpoint, is where the enquiry got to. A process
                // killed inside a model turn leaves the turn's pass count at its last committed
                // boundary, which is what makes "the prompt was sent" distinguishable from "it
                // was never sent".
                Console.WriteLine($"  {taskId} resumes: the enquiry is committed at phase " +
                                  $"{EnquiryPhases.Name(state.Phase)}, after " +
                                  $"{CheckpointInt(record, "turns")} completed pass(es)");
                var outcome = store.WaitAsync(taskId).GetAwaiter().GetResult();
                Console.WriteLine($"  {taskId} finished: {outcome.Status}");
                Print(store.Store, record.OwnerConversationId!);
            }
        }

        // Every enquiry is submitted before any is awaited. That is the shape the baseline had —
        // several fire-and-forget tasks writing one dictionary at once — so the concurrency run
        // exercises the same interleaving the baseline's defect lives in.
        var started = enquiries
            .Select(enquiry => app.RunAgentAsync(enquiry).GetAwaiter().GetResult())
            .ToList();

        var records = started
            .Select(conversationId => store.Store.Tasks.First(task =>
                task.OwnerConversationId == conversationId && task.Name == EnquiryDurable.TaskName))
            .ToList();

        Console.WriteLine(started.Count == 1
            ? $"submitted 1 enquiry"
            : $"submitted {started.Count} enquiries, all before any was awaited");

        foreach (var record in records)
        {
            Console.WriteLine($"waiting on task {record.Id} (checkpoint at start: {CheckpointString(record, "enquiryPhase")}/{CheckpointInt(record, "turns")})");
            Console.Out.Flush();
            var outcome = store.WaitAsync(record.Id).GetAwaiter().GetResult();
            Console.WriteLine($"task {record.Id} finished: {outcome.Status}");
            var settled = store.Store.Task(record.Id)!;
            Console.WriteLine($"  store record: {settled.Id} {settled.Status} " +
                              $"enquiryPhase={CheckpointString(settled, "enquiryPhase")} " +
                              $"turns={CheckpointInt(settled, "turns")}");
        }

        if (started.Count > 1)
        {
            VerifyIsolation(store.Store, started);
        }

        if (lateWakeup)
        {
            LateWakeup(store, database, started[0], toolPath);
        }

        if (contend > 1)
        {
            ContendAsync(store, started[0], contend).GetAwaiter().GetResult();
        }

        foreach (var conversationId in started)
        {
            Print(store.Store, conversationId);
        }

        Console.WriteLine();
        Console.WriteLine("task records in the store:");
        foreach (var task in store.Store.Tasks.OrderBy(task => task.Id, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {task.Id} {task.Name} {task.Status} " +
                              $"enquiryPhase={CheckpointString(task, "enquiryPhase")} turns={CheckpointInt(task, "turns")} " +
                              $"restarts={task.Restarts}");
        }

        Console.WriteLine("disposing store…");
        Console.Out.Flush();
        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("disposed.");
        return true;
    }

    /// <summary>
    /// Proves a terminal enquiry cannot be woken back into the pipeline.
    /// </summary>
    /// <remarks>
    /// This is requirement 3 in one screen. The baseline's defect was a timer that ran after the
    /// enquiry finished and could still touch the record. Here every write is guarded inside the
    /// commit, so the same write the baseline could not stop is refused and the enquiry is left
    /// exactly as it was. The check is an assertion: if any write lands, the run fails.
    /// </remarks>
    private static void LateWakeup(
        EnquiryStore store,
        PropertyDatabase database,
        string conversationId,
        bool assertToolPath)
    {
        var before = EnquiryDurable.Read(store.Store, conversationId);
        Console.WriteLine();
        Console.WriteLine($"late wakeup: the enquiry is committed at {EnquiryPhases.Name(before.Phase)}");
        if (!before.IsTerminal)
        {
            throw new InvalidOperationException(
                $"late wakeup: {conversationId} is not terminal, so it cannot demonstrate the guard");
        }

        // Everything a late wakeup could plausibly try: the timer the baseline used, a stale
        // phase report from a tool call that finished after the enquiry did, and the two
        // transitions a resumed driver might attempt.
        var attempts = new (string Phase, string Why)[]
        {
            (EnquiryPhases.Done, "a delivery completing after the rejection"),
            (EnquiryPhases.Validating, "a resumed task restarting the pipeline"),
            (EnquiryPhases.Searching, "a stale phase report"),
            (EnquiryPhases.WritingReport, "a driver transition"),
        };

        foreach (var (phase, why) in attempts)
        {
            var write = EnquiryWrites.MoveToAsync(store.Store, conversationId, expected: null, phase)
                .GetAwaiter().GetResult();
            Console.WriteLine($"  wakeup {phase,-18} ({why}) -> " +
                              $"{(write.Written ? "WRITTEN" : "refused")}; enquiry stays {write.Phase}");
            if (write.Written)
            {
                throw new InvalidOperationException(
                    $"late wakeup overwrote a terminal state: {phase} replaced {before.Phase}");
            }
        }

        // The phase writers are only half the wiring. These two go through the tool bodies the
        // model actually calls, so the entries a tool appends are tested against the terminal
        // guard rather than assumed to be safe because a phase write was refused.
        var toolAttempts = new (string Tool, string Argument, string Why)[]
        {
            ("search_properties", string.Empty, "a search that finished after the rejection"),
            ("report_intent", "Late intent report", "an intent reported after the rejection"),
        };

        foreach (var (tool, argument, why) in toolAttempts)
        {
            var answer = EnquiryTools.CallForProbe(store.Store, database, conversationId, tool, argument);
            var held = EnquiryDurable.Read(store.Store, conversationId);
            Console.WriteLine($"  wakeup tool {tool,-16} ({why}) -> " +
                              $"{(held.Entries.Count == before.Entries.Count ? "refused" : "WRITTEN")}; " +
                              $"tool answered \"{Truncate(answer, 30)}\", entries {held.Entries.Count}");
            if (held.Entries.Count != before.Entries.Count)
            {
                throw new InvalidOperationException(
                    $"late wakeup grew the transcript through {tool}: " +
                    $"{before.Entries.Count} -> {held.Entries.Count}");
            }
        }

        if (assertToolPath)
        {
            // The rejection turn itself called these two tools after reporting the rejection. Both
            // lost the race with the terminal phase, and neither grew the transcript: that is the
            // measurement this stage exists to make, because a phase-only probe cannot see it.
            var settled = EnquiryDurable.Read(store.Store, conversationId);
            var intents = settled.Entries.Count(entry => entry.Kind == "intent");
            var tools = settled.Entries.Count(entry => entry.Kind == "tool");
            Console.WriteLine($"  the rejection turn called report_intent and search_properties after " +
                              $"reporting it: {intents} intent(s), {tools} tool result(s) in the transcript");
            if (intents != 1 || tools != 0)
            {
                throw new InvalidOperationException(
                    $"a tool write reached the terminal record: {intents} intent(s), {tools} tool result(s)");
            }

            Console.WriteLine($"  the enquiry's own answer, \"Rejected as spam.\", is kept: the turn that " +
                              "ended the enquiry still gets to say why");
        }

        var report = EnquiryWrites.AttachReportAsync(store.Store, conversationId, "invented report")
            .GetAwaiter().GetResult();
        Console.WriteLine($"  wakeup attach-report       -> " +
                          $"{(report.Written ? "WRITTEN" : "refused")}; report unchanged");

        var after = EnquiryDurable.Read(store.Store, conversationId);
        if (EnquiryPhases.Name(after.Phase) != EnquiryPhases.Name(before.Phase))
        {
            throw new InvalidOperationException(
                $"late wakeup changed the phase: {EnquiryPhases.Name(before.Phase)} -> {EnquiryPhases.Name(after.Phase)}");
        }

        if (after.Entries.Count != before.Entries.Count)
        {
            throw new InvalidOperationException(
                $"late wakeup grew the transcript: {before.Entries.Count} -> {after.Entries.Count}");
        }

        Console.WriteLine($"  final phase: {EnquiryPhases.Name(after.Phase)}, " +
                          $"entries unchanged at {after.Entries.Count}: the terminal state held");
    }

    /// <summary>
    /// Checks that each enquiry's document holds only its own writes.
    /// </summary>
    /// <remarks>
    /// This is the cross-talk check, and it is an assertion rather than a claim. Every entry has a
    /// durable id, and every document lists the entries it committed. Two enquiries that wrote
    /// into each other's document would share ids, or one document would name an entry the store
    /// attributes to the other conversation. The check fails the run if either happens.
    /// </remarks>
    private static void VerifyIsolation(DurableStore store, IReadOnlyList<string> conversations)
    {
        var documents = conversations
            .Select(id => (Conversation: id, State: EnquiryDurable.Read(store, id)))
            .ToList();

        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (conversation, state) in documents)
        {
            foreach (var entry in state.Entries)
            {
                if (!owner.TryAdd(entry.EntryId, conversation))
                {
                    throw new InvalidOperationException(
                        $"cross-talk: entry {entry.EntryId} appears in {owner[entry.EntryId]} and {conversation}");
                }

                if (!store.Entries(conversation).Contains(entry.EntryId))
                {
                    throw new InvalidOperationException(
                        $"cross-talk: {conversation} lists entry {entry.EntryId}, which the store attributes " +
                        "to a different conversation");
                }
            }
        }

        // The conversation ids a document can be written from are the ones whose transcript it
        // owns, so a shared entry would have shown up above. What is left is the identity check:
        // each document must carry the enquiry it was created for.
        var enquiries = documents.Select(d => d.State.Enquiry).ToList();
        if (enquiries.Distinct(StringComparer.Ordinal).Count() != enquiries.Count)
        {
            throw new InvalidOperationException(
                $"cross-talk: two documents carry the same enquiry text: {string.Join(" | ", enquiries)}");
        }

        var tasks = documents.Select(d => d.State.TaskId).ToList();
        if (tasks.Distinct(StringComparer.Ordinal).Count() != tasks.Count)
        {
            throw new InvalidOperationException(
                $"cross-talk: two documents name the same task: {string.Join(" | ", tasks)}");
        }

        Console.WriteLine();
        Console.WriteLine($"isolation: {documents.Count} documents checked");
        foreach (var (conversation, state) in documents)
        {
            Console.WriteLine($"  {conversation}: {state.Entries.Count} entries, phase " +
                              $"{EnquiryPhases.Name(state.Phase)}, task {state.TaskId}, " +
                              $"enquiry \"{Truncate(state.Enquiry, 40)}\"");
        }

        Console.WriteLine("  entry ids are disjoint, every entry belongs to its own conversation,");
        Console.WriteLine("  and no two documents share an enquiry or a task: no cross-talk");
    }

    /// <summary>
    /// Races writers at one document, in two experiments that discriminate different rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the baseline's defect 4: several writers mutate one enquiry at once. The baseline
    /// assigned fields on a shared object with no lock, so the last writer won silently. Here
    /// every write goes through the store's commit gate.
    /// </para>
    /// <para>
    /// The gate is only worth demonstrating with an experiment it can fail. Racing
    /// <paramref name="writers"/> copies of one forward transition from one phase is decided by
    /// the <em>rank rule</em> alone — the first commit moves the phase, and every later writer is
    /// refused because its target is no longer forward. A single-writer serial loop produces the
    /// same answer, so that race cannot show the gate: it is reported first, and it is labelled as
    /// the rank-rule demonstration it is.
    /// </para>
    /// <para>
    /// The second experiment is the one the gate decides. Half the writers aim at
    /// <c>writing_report</c> and half at <c>done</c>; both targets are forward from the starting
    /// phase and neither invalidates the other once one has landed, so a rank rule alone would let
    /// both land. Exactly two writes land, and the transcript grows by exactly two — not by the
    /// number of writers, and not by one. A serial loop that re-read the phase but did not
    /// serialize its read against the write would land every writer to <c>done</c> as well, so the
    /// count of two is what a correct gate produces and a broken one cannot.
    /// </para>
    /// </remarks>
    private static async Task ContendAsync(EnquiryStore store, string conversationId, int writers)
    {
        var half = writers / 2;
        if (half == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"contention: skipped, {writers} writer(s) cannot be split across two targets");
            return;
        }

        // Experiment one: one target, many writers. Reported as what it is — the rank rule being
        // applied once — not as evidence about the gate.
        await ContendAsync(
            store,
            conversationId,
            writers,
            "rank rule: every writer aims at the same forward target",
            _ => EnquiryPhases.WritingReport);

        // Experiment two: two forward targets from one phase with no expectation, so the rank
        // order cannot decide the count — both targets are forward, and the second is still
        // forward of the first. Only serialization plus the terminal/forward guard can, and it
        // produces exactly one landing per target.
        await ContendAsync(
            store,
            conversationId,
            writers,
            "commit gate: half the writers aim at writing_report, half at done",
            index => index % 2 == 0 ? EnquiryPhases.WritingReport : EnquiryPhases.Done);
    }

    /// <summary>One race against a reset document, printing what the guards did with it.</summary>
    private static async Task ContendAsync(
        EnquiryStore store,
        string conversationId,
        int writers,
        string label,
        Func<int, string> targetFor)
    {
        // The document is reset to a known phase before every race. The tool conversations are
        // still live, but the workflow's own tool calls only follow turns the replay fixture
        // serves, and the task is already finished, so nothing else writes while the race runs.
        await ResetForRaceAsync(store.Store, conversationId);

        var before = EnquiryDurable.Read(store.Store, conversationId);
        var targets = Enumerable.Range(0, writers).Select(targetFor).ToList();
        var arrived = await Task.WhenAll(targets.Select(target =>
            EnquiryWrites.MoveToAsync(store.Store, conversationId, expected: null, target)));

        var after = EnquiryDurable.Read(store.Store, conversationId);
        var landed = arrived.Count(write => write.Written);
        var gained = after.Entries.Count - before.Entries.Count;

        Console.WriteLine();
        Console.WriteLine($"contention: {writers} writers raced {conversationId} — {label}");
        Console.WriteLine($"  phase before:       {EnquiryPhases.Name(before.Phase)}");
        Console.WriteLine($"  targets:            {targets.Count(t => t == targets[0])} x {targets[0]}" +
                          (targets.Distinct(StringComparer.Ordinal).Count() > 1
                              ? $", {targets.Count(t => t != targets[0])} x {targets.First(t => t != targets[0])}"
                              : string.Empty));
        Console.WriteLine($"  writes that landed: {landed} of {writers}");
        Console.WriteLine($"  phase after:        {EnquiryPhases.Name(after.Phase)}");
        Console.WriteLine($"  transcript gained:  {gained} entr(ies)");

        // The assertion is the point: the expected count is the one a correct serialize-and-guard
        // produces. One target can only land once because the phase moves after the first write;
        // two forward targets from one phase land twice, once per target, and never more.
        var distinctTargets = targets.Distinct(StringComparer.Ordinal).Count();
        if (landed != distinctTargets)
        {
            throw new InvalidOperationException(
                $"contention: {landed} of {writers} writes landed, expected {distinctTargets} " +
                $"(one per distinct forward target)");
        }

        if (gained != landed)
        {
            throw new InvalidOperationException(
                $"contention: the transcript grew by {gained} but {landed} writes landed");
        }

        if (!targets.Contains(EnquiryPhases.Name(after.Phase), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"contention: the final phase {EnquiryPhases.Name(after.Phase)} is not one of the targets");
        }
    }

    /// <summary>Puts the document back at <c>validating</c> so a second race starts from the same phase.</summary>
    /// <remarks>
    /// The reset is an unguarded commit on purpose: it is the experiment setting up its own
    /// starting state, not a write the guards are being tested on. It is what lets the two race
    /// experiments run one after the other against the same document.
    /// </remarks>
    private static Task ResetForRaceAsync(DurableStore store, string conversationId) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            draft.Value.Phase = Phase.Validating;
            draft.Value.Report = null;
            draft.Value.FinishedAt = null;
            draft.Value.HideAt = null;
            draft.Save(tx.AppendEntry(conversationId, "phase", EnquiryPhases.Validating));
            await Task.CompletedTask;
        });

    /// <summary>Prints an enquiry's committed state and transcript.</summary>
    public static void Print(DurableStore store, string conversationId)
    {
        var state = EnquiryDurable.Read(store, conversationId);
        var task = store.Tasks.FirstOrDefault(candidate => candidate.OwnerConversationId == conversationId);

        Console.WriteLine();
        Console.WriteLine($"enquiry {state.EnquiryId} [{conversationId}]");
        Console.WriteLine($"  phase:    {EnquiryPhases.Name(state.Phase)}");
        Console.WriteLine($"  intent:   {state.Intent ?? "(none)"}");
        Console.WriteLine($"  restarts: {state.Restarts}");
        Console.WriteLine($"  task:     {task?.Id ?? "(none)"} {task?.Status.ToString() ?? ""}");
        Console.WriteLine($"  entries:  {state.Entries.Count}");
        foreach (var entry in state.Entries)
        {
            var text = entry.Text is null ? "" : $" {Truncate(entry.Text)}";
            Console.WriteLine($"    {entry.Kind}:{text}");
        }

        if (state.Report is { Length: > 0 } report)
        {
            Console.WriteLine($"  report:   {Truncate(report, 160)}");
        }
    }

    /// <summary>Reads a string property from a task checkpoint.</summary>
    private static string CheckpointString(DurableTaskRecord record, string name) =>
        record.Checkpoint.ValueKind == JsonValueKind.Object
        && record.Checkpoint.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "(null)"
            : "(absent)";

    private static int CheckpointInt(DurableTaskRecord record, string name) =>
        record.Checkpoint.ValueKind == JsonValueKind.Object
        && record.Checkpoint.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : -1;

    private static string Truncate(string text, int length = 80) =>
        text.Length <= length ? text : text[..length] + "…";
}
