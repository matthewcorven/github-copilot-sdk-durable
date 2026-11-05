# Band A Durable variant — captured restart, terminal-state, concurrency, and recovery evidence

Everything below was produced on 2026-11-05 on macOS arm64 with .NET SDK 10.0.402 at
`/opt/homebrew/bin/dotnet`, by running `durable/evidence.sh`. The blocks are the programs' own
stdout, filtered only to drop the NuGet advisory the build prints (NU1903 for
`SQLitePCLRaw.lib.e_sqlite3`) and the EF Core model-validation warning. Nothing else is edited:
scratch paths are rewritten to `<scratch>` so the transcript is readable.

## What produced it

```sh
cd durable
./evidence.sh          # every stage below
```

**Every stage here runs against the recorded runtime** (`tools/ReplayRuntime`) with the fixtures in
`fixtures/`, so it needs no Copilot sign-in, no model and no network. That is also this file's most
important caveat, and it is why the counts below are attributed rather than subtracted — see
"What stage 6 does and does not measure".

`LIVE=1 ./evidence.sh` runs stage 4 against the signed-in `copilot` executable instead. Stage 4 is
the only stage with a live mode; stages 6e/6f and 3f/3g are recorded-runtime too, and a live session
would answer them differently in one respect noted below.

The kill points are environment variables, and they fire inside the process:

| Variable | Fires |
| --- | --- |
| `BAND_A_EXIT_AFTER_PHASE=<phase>` | after that phase commits, before anything else |
| `BAND_A_EXIT_AFTER_ANSWER=<phase>` | after that phase's turn **answer** commits, before the task boundary |
| `BAND_A_EXIT_BEFORE_ANSWER=<phase>` | after the turn returns, before its answer is committed |

All three are real `Environment.Exit` calls on the commit path, so the store is guaranteed to hold
whatever the process died after — there is no timer and no window to miss.

## The stages, and what each proves

| Stage | Proves |
| --- | --- |
| 0 | The uninterrupted run every later stage is compared against: 9 entries, 2 prompts. |
| 1 | A mid-enquiry process death is reproducible on purpose: exit 42 immediately after `searching` commits. |
| 2 | A new process resumes the same enquiry at `searching` and finishes it — requirement 2, and one entry above stage 0 because the recorded runtime replays a turn. |
| 3 | A rejected enquiry is terminal: four phase wakeups, two tool writes and a report attach are all refused — requirement 3. |
| 3b | The same, with a fixture whose rejection turn also calls `report_intent` and `search_properties`. |
| 3c | The rejection survives a restart. The baseline deletes it after fifteen seconds; this cannot. |
| 3d | A kill after the model reports `done`, **before** its answer commits, leaves a terminal document with no report. |
| 3e | Resuming that store recovers the report by asking the model once — 3 prompts against stage 0's 2. |
| 3f | The same window killed **after** the answer commits. |
| 3g | Resuming *that* store recovers the report from the committed answer with **no extra prompt** — 2 prompts, like stage 0. |
| 4 | Two enquiries for two customers complete with disjoint transcripts and tasks — requirement 4. |
| 5 | Two races at one document: the rank rule landing once, and the commit gate landing twice from two targets. |
| 6 | Kill inside a turn whose answer never committed; the resumed process replays the session. |
| 6b–6d | What that resume costs, and how much of it the recorded runtime contributes. |
| 6e | The two-send fixture: each turn ends its own send, so the document stays non-terminal between them. |
| 6f | A kill between a turn's answer and its boundary — the only state the reconciliation can act on. |
| 6g | Resuming that store **must** run the reconciliation; the stage fails if the marker line is absent. |
| 6h | A forged store whose baseline precedes several turns must be **refused**, not guessed at. |

## What the runs show about checkpoint boundaries

Two facts about the boundary are visible in these stages, and neither is a claim that the recorded
runtime can prove on its own.

1. **A kill at a transition resumes, and the resume is not free.** Stage 1 kills after `searching`
   is committed and stage 2 finishes the enquiry — but at **10** entries where stage 0's
   uninterrupted run of the same fixture produces **9**, and the transcript shows
   `intent: Validate home enquiry` twice. The extra entry is replayed work, not a re-render: the
   resumed process reconnects to a session the recorded runtime serves from its first turn, so the
   earlier turn is sent and committed again. Same mechanism as stage 6, same cause. **Stage 2's 10
   does not mean "nothing repeated"; it means "the enquiry finished, with one turn replayed by the
   recorded runtime".**
2. **A kill inside a turn is worse, and the size of the difference is not all the turn.** Stage 6
   kills with the phase at `writing_report` and the pre-answer tool calls already committed; the
   interrupted turn produced no answer, so the platform has no record that it ran. Stage 6b resumes,
   reconnects, and is served the recording from its first turn: the intent lines and the tool line
   are committed a second time and the transcript reaches **12** against stage 0's **9**.

The other side of the coin is stages 6e-6h, and it is a boundary result the recorded runtime can
settle, because it does not depend on how a session is served. When a turn's answer and its
`turn:<messageId>` checkpoint both committed but the task's boundary did not, the resume
recognises the turn and reuses its answer instead of sending the prompt again. Two things about
those stages are deliberate:

- **They use the two-send fixture, not `enquiry.json`.** The reconciliation needs the interrupted
  turn to leave a *non-terminal* document — otherwise the resume takes the terminal recovery path
  and never consults the conversation — and it needs a conversation baseline, which only exists
  from the second turn onward. `enquiry.json` runs the whole pipeline inside one send, so it can
  satisfy neither. An earlier revision claimed this case was covered by a stage that killed after
  `done`; it was not, and the assertion passed with the reconciliation disabled. Stage 6g now
  fails if the marker line is missing.
- **Stage 6h is the negative half.** The wrong-turn bug was real: a baseline older than several
  turn checkpoints let the guard attach a *different* turn's answer. The lookup now groups
  candidates by event id and returns an answer only when they name exactly one turn; stage 6h
  forges a store with four and asserts the guard declines.

## What stage 6 does and does not measure

Stage 6d reports the two totals and the duplicated intent count, and it deliberately does **not**
call the surplus "the interrupted turn". Two effects are folded into it:

- **Session replay.** The resumed process reconnects and `SessionCreate` serves it
  `_fixture.Sessions[0]` from turn 0 (announced on `REPLAY_MARKER_PATH` as `synthesised session`).
  Every turn before the interruption is sent and committed again. This is the same recorded-runtime
  behaviour the README names as stage 4's limitation, and it contributes the duplicated
  `intent: Validate home enquiry`, which appears in every resumed run including stage 1's.
- **The interrupted turn.** The part the checkpoint boundary is actually responsible for. The
  duplicated `tool: 1 match(es)` is the entry unambiguously traceable to it.

The recorded runtime cannot separate the two, so the `12 - 9` difference **overstates** the boundary
effect and this file does not present it as a boundary measurement. The true boundary cost can only
be measured against a live CLI, where a resumed session continues instead of restarting from turn 0;
stage 6's number is a recorded-runtime **upper bound**.

## Captured output

```
############ 0. an uninterrupted run of the fixture, for every comparison below ############
PropertyDatabase: seeded 100 properties from /private/var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-a-durable-evidence.mcZ5Nc/app/Data/Properties
store:          <scratch>/clean.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 9a2c1d48
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
enquiry 9a2c1d48 settled at done
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

enquiry 9a2c1d48 [c1]
  phase:    done
  intent:   Searching listings
  restarts: 0
  task:     t11 Terminal
  entries:  9
    phase:
    intent: Validate home enquiry
    phase:
    intent: Searching listings
    tool: 1 match(es)
    phase:
    phase:
    answer: The report is written and the enquiry is done.
    report: The report is written and the enquiry is done.
  report:   The report is written and the enquiry is done.

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.
entries: 9, prompts admitted: 2

############ 1. restart mid-enquiry: exit after the 'searching' phase commits ############
store:          <scratch>/restart.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 6de87437
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_PHASE=searching: committed, exiting the process mid-enquiry (exit code 42)
exit code: 42  (42 is the seam: the store is committed, and the process is gone)

############ 2. resume the same store in a new process ############
store:          <scratch>/restart.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase searching, after 0 completed pass(es)
enquiry 6de87437 settled at done
  t11 finished: completed

enquiry 6de87437 [c1]
  phase:    done
  intent:   Searching listings
  restarts: 1
  task:     t11 Terminal
  entries:  10
    phase:
    intent: Validate home enquiry
    phase:
    intent: Validate home enquiry
    intent: Searching listings
    tool: 1 match(es)
    phase:
    phase:
    answer: The report is written and the enquiry is done.
    report: The report is written and the enquiry is done.
  report:   The report is written and the enquiry is done.
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=searching turns=1 restarts=1
disposing store…
disposed.

############ 3. late wakeup after rejection cannot overwrite the terminal state ############
store:          <scratch>/rejected.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 14e9ad7d
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

late wakeup: the enquiry is committed at rejected_garbage
  wakeup done               (a delivery completing after the rejection) -> refused; enquiry stays rejected_garbage
  wakeup validating         (a resumed task restarting the pipeline) -> refused; enquiry stays rejected_garbage
  wakeup searching          (a stale phase report) -> refused; enquiry stays rejected_garbage
  wakeup writing_report     (a driver transition) -> refused; enquiry stays rejected_garbage
enquiry 14e9ad7d settled at rejected_garbage
  wakeup tool search_properties (a search that finished after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  wakeup tool report_intent    (an intent reported after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  wakeup attach-report       -> refused; report unchanged
  final phase: rejected_garbage, entries unchanged at 4: the terminal state held

enquiry 14e9ad7d [c1]
  phase:    rejected_garbage
  intent:   Checking enquiry
  restarts: 0
  task:     t11 Terminal
  entries:  4
    phase:
    intent: Checking enquiry
    phase:
    answer: Rejected as spam.

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.

############ 3b. a tool result from the rejection turn cannot grow the terminal record ############
store:          <scratch>/rejected-tool.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry cb8ce9e7
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
enquiry cb8ce9e7 settled at rejected_garbage
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

late wakeup: the enquiry is committed at rejected_garbage
  wakeup done               (a delivery completing after the rejection) -> refused; enquiry stays rejected_garbage
  wakeup validating         (a resumed task restarting the pipeline) -> refused; enquiry stays rejected_garbage
  wakeup searching          (a stale phase report) -> refused; enquiry stays rejected_garbage
  wakeup writing_report     (a driver transition) -> refused; enquiry stays rejected_garbage
  wakeup tool search_properties (a search that finished after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  wakeup tool report_intent    (an intent reported after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  the rejection turn called report_intent and search_properties after reporting it: 1 intent(s), 0 tool result(s) in the transcript
  the enquiry's own answer, "Rejected as spam.", is kept: the turn that ended the enquiry still gets to say why
  wakeup attach-report       -> refused; report unchanged
  final phase: rejected_garbage, entries unchanged at 4: the terminal state held

enquiry cb8ce9e7 [c1]
  phase:    rejected_garbage
  intent:   Checking enquiry
  restarts: 0
  task:     t11 Terminal
  entries:  4
    phase:
    intent: Checking enquiry
    phase:
    answer: Rejected as spam.

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.

############ 3c. the rejection survives a restart (the baseline loses it) ############
store:          <scratch>/rejected-tool.jsonl
store persists: True
conversations:  1
tasks:          1
resumed tasks:  0
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.

############ 3d. a kill after the model reports done does not lose the report ############
store:          <scratch>/done.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 01031e97
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_PHASE=done: committed, exiting the process mid-enquiry (exit code 42)
exit code: 42  (42 is the seam: the model has reported done and the process is gone)

############ 3e. resume that store: the deliverable is recovered by asking again ############
store:          <scratch>/done.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase done, after 0 completed pass(es)
enquiry 01031e97 settled at done
  t11 finished: completed

enquiry 01031e97 [c1]
  phase:    done
  intent:   Searching listings
  restarts: 1
  task:     t11 Terminal
  entries:  10
    phase:
    intent: Validate home enquiry
    phase:
    intent: Searching listings
    tool: 1 match(es)
    phase:
    phase:
    note: the report was lost; asking the model for it again
    report: The report is written and the enquiry is done.
    note: the report was recovered
  report:   The report is written and the enquiry is done.
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=0 restarts=1
disposing store…
disposed.
prompts admitted for this enquiry: 3 (this is the re-ask path; stage 3g, where the answer survived, admits one fewer)

############ 3f. the same window, killed AFTER the answer committed: recovered from it ############
exit code: 42  (42 is the seam: the answer is committed and the task boundary is not)
store:          <scratch>/done-answer.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 36c42cae
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_ANSWER=queued: the answer is committed and the process exits before the task boundary (exit code 42)

############ 3g. resume it: the committed answer becomes the report, with no second prompt ############
store:          <scratch>/done-answer.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase done, after 0 completed pass(es)
  t11 finished: completed
enquiry 36c42cae settled at done

enquiry 36c42cae [c1]
  phase:    done
  intent:   Searching listings
  restarts: 1
  task:     t11 Terminal
  entries:  10
    phase:
    intent: Validate home enquiry
    phase:
    intent: Searching listings
    tool: 1 match(es)
    phase:
    phase:
    answer: The report is written and the enquiry is done.
    report: The report is written and the enquiry is done.
    note: the report was recovered from the answer already committed
  report:   The report is written and the enquiry is done.
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=0 restarts=1
disposing store…
disposed.
prompts admitted for this enquiry: 2 (an uninterrupted run of the same fixture admits 2)
the recovery did not re-ask: the answer it attached is the one the killed turn committed

############ 4. concurrency: two enquiries for two customers, submitted together ############
# recorded runtime: both pinned sessions are served from the first recording, so
# this stage shows isolation of the durable documents, not of the model transcripts.
store:          <scratch>/concurrent.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 4034062e
created enquiry 8d6b7ed9
submitted 2 enquiries, all before any was awaited
waiting on task t11 (checkpoint at start: queued/0)
enquiry 8d6b7ed9 settled at done
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1
waiting on task t34 (checkpoint at start: queued/1)
task t34 finished: completed
  store record: t34 Terminal enquiryPhase=queued turns=1

isolation: 2 documents checked
  c1: 8 entries, phase done, task t11, enquiry "first customer: 3-bed family home"
  c18: 8 entries, phase done, task t34, enquiry "second customer: downtown condo with par…"
  entry ids are disjoint, every entry belongs to its own conversation,
  and no two documents share an enquiry or a task: no cross-talk

enquiry 4034062e [c1]
  phase:    done
  intent:   Checking customer one
  restarts: 0
  task:     t11 Terminal
  entries:  8
    phase:
    intent: Checking customer one
    phase:
    tool: 1 match(es)
    phase:
    phase:
    answer: Customer one is done.
    report: Customer one is done.
  report:   Customer one is done.

enquiry 8d6b7ed9 [c18]
  phase:    done
  intent:   Checking customer one
  restarts: 0
  task:     t34 Terminal
  entries:  8
    phase:
    intent: Checking customer one
    phase:
    tool: 1 match(es)
    phase:
    phase:
    answer: Customer one is done.
    report: Customer one is done.
  report:   Customer one is done.

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
  t34 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.

############ 5. concurrency: rank rule vs commit gate ############
store:          <scratch>/contend.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 6b2a6ad9
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

contention: 8 writers raced c1 — rank rule: every writer aims at the same forward target
  phase before:       validating
  targets:            8 x writing_report
  writes that landed: 1 of 8
  phase after:        writing_report
  transcript gained:  1 entr(ies)

contention: 8 writers raced c1 — commit gate: half the writers aim at writing_report, half at done
  phase before:       validating
  targets:            4 x writing_report, 4 x done
  writes that landed: 2 of 8
  phase after:        done
  transcript gained:  2 entr(ies)

enquiry 6b2a6ad9 [c1]
  phase:    done
  intent:   Searching listings
  restarts: 0
  task:     t11 Terminal
  entries:  12
    phase:
    intent: Validate home enquiry
    phase:
    intent: Searching listings
    tool: 1 match(es)
    phase:
    phase:
    answer: The report is written and the enquiry is done.
    report: The report is written and the enquiry is done.
    phase:
    phase:
    phase:

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=1 restarts=0
disposing store…
disposed.

############ 6. kill inside an unfinished turn: the resumed process replays the session ############
store:          <scratch>/midturn.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry c113382c
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_PHASE=writing_report: committed, exiting the process mid-enquiry (exit code 42)
exit code: 42  (42 is the seam: the phase is committed and the process is gone)
entries committed before the kill: 6

############ 6b. resume it: the reconnected session is served from its first recorded turn ############
store:          <scratch>/midturn.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase writing_report, after 0 completed pass(es)
  t11 finished: completed

enquiry c113382c [c1]
  phase:    done
  intent:   Searching listings
  restarts: 1
  task:     t11 Terminal
  entries:  12
    phase:
    intent: Validate home enquiry
    phase:
    intent: Searching listings
    tool: 1 match(es)
    phase:
    intent: Validate home enquiry
    intent: Searching listings
    tool: 1 match(es)
    phase:
    answer: The report is written and the enquiry is done.
    report: The report is written and the enquiry is done.
  report:   The report is written and the enquiry is done.
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=writing_report turns=1 restarts=1
disposing store…
disposed.

############ 6d. the count, and what it does and does not attribute ############
clean run:                          9 entries
killed at writing_report, resumed:  12 entries
duplicated "intent: Validate home enquiry" lines in the resumed run: 2 (1 in the clean run)

The surplus is NOT attributable to the interrupted turn alone. The resumed process reconnects
to a session the recorded runtime serves from its first recording (REPLAY_MARKER_PATH says
'synthesised session'), so the earlier turns are sent and committed a second time. What the
surplus measures is: session replay + the interrupted turn, and the recorded runtime cannot
separate them. The duplicated tool line is the part traceable to the interrupted turn.

############ 6e. the two-send fixture: each turn ends a send, leaving a non-terminal document ############
store:          <scratch>/twosends.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry b140e345
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
enquiry b140e345 settled at done
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=writing_report turns=4

enquiry b140e345 [c1]
  phase:    done
  intent:   Write report
  restarts: 0
  task:     t11 Terminal
  entries:  13
    phase:
    intent: Validate enquiry
    answer: I have validated the enquiry.
    phase:
    intent: Search listings
    tool: 1 match(es)
    answer: The search is done.
    phase:
    intent: Write report
    answer: Draft report: 12 Maple Row and 88 Birch Lane.
    phase:
    answer: The enquiry is complete.
    report: The enquiry is complete.
  report:   The enquiry is complete.

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=writing_report turns=4 restarts=0
disposing store…
disposed.

############ 6f. kill between a turn's answer and its boundary, then resume ############
exit code: 42  (42 is the seam: the answer is committed and the boundary is not)
store:          <scratch>/settled.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 0bfb70ef
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_ANSWER=searching: the answer is committed and the process exits before the task boundary (exit code 42)
entries committed before the kill: 7

############ 6g. resume it: the reconciliation must run, and this fails if it does not ############
store:          <scratch>/settled.jsonl
store persists: True
conversations:  1
tasks:          1
reconciliation: the interrupted turn already answered; reusing that answer instead of sending the prompt again
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase searching, after 2 completed pass(es)
enquiry 0bfb70ef settled at done
  t11 finished: completed

enquiry 0bfb70ef [c1]
  phase:    done
  intent:   Write report
  restarts: 1
  task:     t11 Terminal
  entries:  16
    phase:
    intent: Validate enquiry
    answer: I have validated the enquiry.
    phase:
    intent: Search listings
    tool: 1 match(es)
    answer: The search is done.
    intent: Validate enquiry
    phase:
    intent: Search listings
    tool: 1 match(es)
    intent: Write report
    answer: Draft report: 12 Maple Row and 88 Birch Lane.
    phase:
    answer: The enquiry is complete.
    report: The enquiry is complete.
  report:   The enquiry is complete.
submitted 0 enquiries, all before any was awaited

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=writing_report turns=6 restarts=1
disposing store…
disposed.
6g held: the reconciliation ran and reused the committed answer
  the interrupted turn's answer appears 1 time(s)

############ 6h. an ambiguous store: two candidate turns must be refused ############
  forged a store whose baseline precedes 4 distinct turn checkpoint(s)

task records in the store:
  t11 band-a.enquiry Terminal enquiryPhase=searching turns=1 restarts=2
disposing store…
disposed.
6h held: with more than one candidate turn the guard refused rather than guessing
```
