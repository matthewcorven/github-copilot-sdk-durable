// Band C Durable variant — the same pr-triage agent as ../baseline/, with its state in a
// Durable store instead of a JSON blob on a git branch (issue #155).
//
// The eight phases, the cooldown rule, the classifier, the scorer, the batch and label rules, the
// comment and the report are the baseline's. Triage.cs is that code moved, not rewritten: the only
// changes are the two places where a run reads or writes state. Before quoting a difference, keep
// what that means in view — a comparison of these two programs is a comparison of where state
// lives, not of triage quality, because the triage is literally the same code. FIDELITY.md says
// which claims that rules in and out.
//
// What the state is now:
//
//   band-c.run          one document per repository: the run's clock, the phase it reached, the
//                       generation, and the comments it posted. It is what a new process reads to
//                       decide whether it is continuing an interrupted run or starting one.
//   band-c.pr           one document per pull request: the assessment, the stage it reached, and
//                       the input digest it was made from. A PR whose digest is unchanged is not
//                       re-classified, re-scored or re-commented.
//   band-c.comments     one document owned by the agent, holding every comment it has posted and
//                       the digest each was posted for. It outlives the run, so a later run with a
//                       new task and no memos still does not re-post.
//
// And the two properties the baseline could not have:
//
//   * the comment is a step with a memo, keyed (pull request, digest) — never a timestamp or a
//     conversation id, both of which differ between two attempts at the same work. Re-running an
//     unchanged run finds the memos; resuming a run that died after commenting finds the same ones.
//   * overlap is refused at the store, not in a flag: a persistent Durable store holds an exclusive
//     lock on its file for its lifetime, so a second process cannot open the store at all. README.md
//     §"Concurrency" states what that costs as well as what it buys.

using BandC.Durable;

return await Runner.RunAsync(args);
