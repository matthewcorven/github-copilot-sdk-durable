using System.Globalization;
using System.Text.Json;
using Durable;

namespace BandC.Durable;

/// <summary>
/// Every read and write of durable state, in one place.
/// </summary>
/// <remarks>
/// This file is durability, and it is the whole of the custom durability surface: three documents,
/// the reads that feed a phase, and the writes that make a decision a fact. The phases call it; they
/// do not touch the store themselves, so there is one place to look for what is durable.
/// </remarks>
public static class TriageStore
{
    /// <summary>The run id for a repository: the run document's owner.</summary>
    /// <remarks>
    /// Derived from the fixture's repository name, not from a clock or a process, so the next run of
    /// the same repository finds the previous one's record instead of starting a second.
    /// </remarks>
    public static string RunId(string repository) => Store.OwnerOf(repository);

    /// <summary>Reads a run record, or null when the repository has never run.</summary>
    public static RunState? Run(DurableStore store, string runId)
    {
        var document = store.Document(Store.RunKind, runId);
        return document?.Latest is { } value ? value.Deserialize<RunState>(DurableJson.Options) : null;
    }

    /// <summary>
    /// Reads a pull request's committed assessment, or an empty one.
    /// </summary>
    /// <remarks>
    /// A document exists only once something was committed to it, so "absent" and "present but
    /// empty" are the same answer here: the PR has never been assessed at any digest.
    /// </remarks>
    public static PullRequestState PullRequest(DurableStore store, int number)
    {
        var owner = Store.PullRequestOwner(number);
        var document = store.Document(Store.PullRequestKind, owner);
        return document?.Latest is { } value
            ? value.Deserialize<PullRequestState>(DurableJson.Options) ?? new PullRequestState { Number = number }
            : new PullRequestState { Number = number };
    }

    /// <summary>Reads the comment ledger, or an empty one.</summary>
    public static CommentLedger Ledger(DurableStore store) =>
        store.Document(Store.LedgerKind, Store.LedgerOwner)?.Latest is { } value
            ? value.Deserialize<CommentLedger>(DurableJson.Options) ?? new CommentLedger()
            : new CommentLedger();

    /// <summary>
    /// Writes the run record's phase and whatever the phase decided about the run.
    /// </summary>
    /// <remarks>
    /// One transaction, and the entry it appends is what the document version points at. The
    /// version and the entry commit together or not at all, which is the difference between "the
    /// phantom run at phase 3" and "a run that never reached phase 3".
    /// </remarks>
    public static Task CommitRunAsync(DurableStore store, RunInput input, RunState state) =>
        store.CommitAsync(tx =>
        {
            var draft = tx.Doc(Store.Run, Store.OwnerOf(state.Repository));
            draft.Value = state;
            draft.Save(tx.AppendEntry(input.Journal, "run-phase", state.Phase));
            return Task.CompletedTask;
        });

    /// <summary>
    /// Writes a pull request's assessment at the stage the phase reached.
    /// </summary>
    /// <remarks>
    /// The caller passes the document it already holds, so a phase cannot accidentally write a
    /// stage it did not compute: the document is built from what the phase just decided.
    /// </remarks>
    public static Task CommitPullRequestAsync(
        DurableStore store, RunInput input, PullRequestState state, string entryKind) =>
        store.CommitAsync(tx =>
        {
            var draft = tx.Doc(Store.PullRequest, Store.PullRequestOwner(state.Number));
            draft.Value = state;
            draft.Save(tx.AppendEntry(input.Journal, entryKind, state.Stage));
            return Task.CompletedTask;
        });

    /// <summary>
    /// Records a posted comment in the ledger, and its step memo, in one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The effect (the comment file) is written before this is called, so a process that dies
    /// between the two leaves a comment that no record mentions. That window is real, it is
    /// measured, and README.md §"The window a memo cannot close" says what it costs.
    /// </para>
    /// <para>
    /// What this transaction does buy is that the ledger entry and the memo cannot disagree. They
    /// are two different facts — one belongs to the repository, one to this task's attempt — and
    /// committing them together means a resume either sees both or neither, never a ledger entry
    /// with no memo or a memo for a comment the ledger does not have.
    /// </para>
    /// <para>
    /// The memo goes through <see cref="Transaction.Memo"/> rather than
    /// <c>TaskRuntime.MemoAsync</c> because the latter takes the commit gate this transaction
    /// already holds. That is the gap the layer change on this branch closes; before it, this
    /// method could not be written at all.
    /// </para>
    /// </remarks>
    public static Task RecordCommentAsync(
        DurableStore store, RunInput input, string taskId, string memoKey, CommentRecord record) =>
        store.CommitAsync(tx =>
        {
            var draft = tx.Doc(Store.Ledger, Store.LedgerOwner);
            draft.Value.Comments.Add(record);
            draft.Save(tx.AppendEntry(input.Journal, "comment", $"#{record.Number}"));
            tx.Memo(taskId, memoKey, DurableJson.ToElement(record));
            return Task.CompletedTask;
        });

    /// <summary>
    /// Creates the journal conversation a new run's entries hang off.
    /// </summary>
    /// <remarks>
    /// The scenario has no model session, so there is no conversation the SDK created; the journal is
    /// the durable record's own conversation, created in the same transaction that creates the task
    /// so a crash cannot leave a task whose entries point at nothing.
    /// </remarks>
    public static Task<string> CreateJournalAsync(DurableStore store) =>
        store.CommitAsync(tx => Task.FromResult(tx.CreateConversation().Id));

    /// <summary>
    /// The task a new attempt should continue: the most recent unfinished one, or the most recent
    /// finished one when every attempt is finished.
    /// </summary>
    /// <remarks>
    /// **An unfinished task wins, whatever its position.** The obvious lookup —
    /// <c>FirstOrDefault(name == …)</c> — returns the first task the store happens to hold, which
    /// after one completed generation is always that finished one: the resume branch then never runs,
    /// a second process creates a new task, and the interrupted one is stranded with nobody to
    /// continue it. That was a real defect, found in review, and the ordering below is the fix.
    /// <para>
    /// Among unfinished tasks the newest wins, so an interrupted run is continued rather than an
    /// older abandoned attempt. Among finished tasks the newest wins too, so <c>--reset</c> measures
    /// a new generation against the last one. Task ids are allocated from the store's monotonic
    /// sequence (<c>t{sequence}</c>), so the numeric suffix is the creation order; ordering by it
    /// rather than by list position is what makes the choice independent of provider iteration order.
    /// </para>
    /// </remarks>
    public static DurableTaskRecord? CurrentTask(DurableStore store) =>
        store.Tasks
            .Where(task => task.Name == Store.TaskName)
            .OrderBy(task => task.Status == global::Durable.TaskStatus.Terminal ? 1 : 0)
            .ThenByDescending(task => SequenceOf(task.Id))
            .FirstOrDefault();

    /// <summary>
    /// A task id's creation order, from the <c>t{sequence}</c> form
    /// <see cref="Transaction.CreateTask"/> allocates.
    /// </summary>
    private static long SequenceOf(string taskId) =>
        taskId.Length > 1 && long.TryParse(taskId.AsSpan(1), out var value) ? value : 0;

    /// <summary>The number in a pull request's owner id.</summary>
    public static int NumberOf(string owner) =>
        int.Parse(owner.Split(':')[1], CultureInfo.InvariantCulture);

    /// <summary>The pull request numbers that have a committed document.</summary>
    public static List<int> Numbers(DurableStore store) =>
        [.. store.Documents
            .Where(document => document.Kind == Store.PullRequestKind)
            .Select(document => NumberOf(document.OwnerId))
            .Order()];

    /// <summary>
    /// The committed versions of the comment ledger.
    /// </summary>
    /// <remarks>
    /// Read by the evidence readers, which count the ledger's versions from the JSONL rather than
    /// from anything this program printed: the ledger is the fact the idempotency proof rests on, so
    /// a reader has to be able to count it without taking this program's word for it.
    /// </remarks>
    public static IReadOnlyList<DocumentVersion> LedgerVersions(DurableStore store) =>
        store.Document(Store.LedgerKind, Store.LedgerOwner)?.Versions ?? [];
}
