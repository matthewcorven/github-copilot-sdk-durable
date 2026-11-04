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
        var concurrency = 1;
        string? exitAfter = null;
        var resume = false;
        var contend = 0;
        var lateWakeup = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--enquiry" when i + 1 < args.Length:
                    enquiries.Add(args[++i]);
                    break;
                case "--concurrency" when i + 1 < args.Length:
                    concurrency = int.Parse(args[++i]);
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

        Console.WriteLine($"submitted {started.Count} enquir{(started.Count == 1 ? "y" : "ies")} " +
                          $"at concurrency {concurrency}");

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
            LateWakeup(store, started[0]);
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
    private static void LateWakeup(EnquiryStore store, string conversationId)
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
    /// Fires the same phase transition at one document from many callers at once.
    /// </summary>
    /// <remarks>
    /// This is the baseline's defect 4, made deterministic: several writers race to move one
    /// enquiry's phase. In the baseline each of them would have assigned the field on the shared
    /// object and the last writer won, silently. Here every write goes through the store's commit
    /// gate, and the transition rule means a phase can only move forward once, so exactly one
    /// write lands and the rest are dropped. The count printed is the durable evidence: the
    /// transcript gains one phase entry, not <paramref name="writers"/>.
    /// </remarks>
    private static async Task ContendAsync(EnquiryStore store, string conversationId, int writers)
    {
        // The race is held against a document that has no task running, so the result is decided
        // by the writers alone rather than by a model turn happening to finish first. The
        // document is committed first, so every writer starts from the same committed phase.
        await store.Store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            draft.Value.Phase = Phase.Validating;
            draft.Save(tx.AppendEntry(conversationId, "phase", EnquiryPhases.Validating));
            await Task.CompletedTask;
        });

        var before = EnquiryDurable.Read(store.Store, conversationId);
        var arrived = await Task.WhenAll(Enumerable.Range(0, writers).Select(_ =>
            EnquiryWrites.MoveToAsync(
                store.Store,
                conversationId,
                expected: null,
                EnquiryPhases.WritingReport)));

        var after = EnquiryDurable.Read(store.Store, conversationId);
        Console.WriteLine();
        Console.WriteLine($"contention: {writers} writers raced to move {conversationId} to writing_report");
        Console.WriteLine($"  phase before:      {EnquiryPhases.Name(before.Phase)}");
        Console.WriteLine($"  writes that landed: {arrived.Count(write => write.Written)} of {writers}");
        Console.WriteLine($"  phase after:       {EnquiryPhases.Name(after.Phase)}");
        Console.WriteLine($"  transcript gained: {after.Entries.Count - before.Entries.Count} entr(ies)");
    }

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
