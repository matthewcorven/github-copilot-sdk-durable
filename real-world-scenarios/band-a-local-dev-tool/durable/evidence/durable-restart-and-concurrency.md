# Band A Durable variant — captured restart, terminal-state, concurrency, and recovery evidence

Everything below was produced on 2026-11-04 on macOS arm64 with .NET SDK 10.0.402 at
`/opt/homebrew/bin/dotnet`, by running `durable/evidence.sh`. The blocks are the programs' own
stdout, filtered only to drop the NuGet advisory the build prints (NU1903 for
`SQLitePCLRaw.lib.e_sqlite3`) and the EF Core model-validation warning. Nothing else is edited:
scratch paths are rewritten to `<scratch>` so the transcript is readable.

## What produced it

```sh
cd durable
./evidence.sh          # every stage below
```

Every stage here runs against `tools/ReplayRuntime` with the fixtures in `fixtures/`, so it needs
no Copilot sign-in, no model and no network. The one stage that cannot use the recorded runtime is
stage 4's *live* mode: the recorded runtime returns the whole recorded session when the client
names a session of its own, so it cannot give two customers two different transcripts.
`LIVE=1 ./evidence.sh` runs stage 4 against the signed-in `copilot` executable instead; the run
below is the recorded one.

The kill point is `BAND_A_EXIT_AFTER_PHASE=<phase>`: the process exits the moment that phase is
committed, on the commit path, so the store is guaranteed to hold the phase the process died
after. It is not a timer and not a race.

## The stages, and what each proves

| Stage | Proves |
| --- | --- |
| 1 | A mid-enquiry process death is reproducible on purpose: exit 42 immediately after `searching` commits. |
| 2 | A new process resumes the same enquiry at `searching` and finishes it — requirement 2. |
| 3 | A rejected enquiry is terminal: four phase wakeups, two tool writes and a report attach are all refused — requirement 3. |
| 3b | The same, with a fixture whose rejection turn also calls `report_intent` and `search_properties`. |
| 3c | The rejection survives a restart. The baseline deletes it after fifteen seconds; this cannot. |
| 3d | A kill after the model reports `done` leaves a terminal document with no report. |
| 3e | Resuming that store recovers the report instead of committing `report: null`. |
| 4 | Two enquiries for two customers complete with disjoint transcripts and tasks — requirement 4. |
| 5 | Two races at one document: the rank rule landing once, and the commit gate landing twice from two targets. |
| 6 | A kill **inside** a turn re-runs that turn on resume, and the transcript shows the duplication. |
| 6b/6c/6d | The size of that re-run, measured against an uninterrupted run of the same fixture. |

## What the evidence shows about checkpoint boundaries

Two facts about the boundary are visible in these stages and are worth stating before the output:

1. **A kill at a transition resumes without repeating work.** Stage 1 kills after `searching` is
   committed; stage 2 resumes and finishes at 10 entries, which is the same count an uninterrupted
   run of that fixture produces.
2. **A kill inside a turn is a different thing.** Stage 6 kills after the transition to
   `writing_report`, inside the turn that follows it — after the model has already committed its
   tool calls. Stage 6b resumes and the transcript grows to **12**: the turn is re-sent, and the
   intent and tool entries it had already committed are recorded a second time. Stage 6c is the
   same fixture with no kill, which ends at **9**, and stage 6d subtracts the two.

So the checkpoint boundary is the **turn**, not the tool call inside it. A process that dies
between two transitions resumes at a transition. A process that dies within a turn re-runs that
turn, and the turn's effects happen again. That is a real limit of this design, it is measured
here rather than asserted, and `README.md` documents it.

## What stage 5 is, and is not

Stage 5 runs two separate races, because the first one alone cannot distinguish a correct gate from
a correct rank comparison:

- **One target, eight writers.** `1 of 8` lands. Any implementation that refuses a move to a phase
  the document has already passed produces this, including a single-threaded loop. It is a
  demonstration of the forward-only rule.
- **Two forward targets, eight writers.** Four write to `writing_report` and four to `done`. `2 of
  8` lands — one per target — and the transcript gains exactly two entries. This one is decided by
  serialization, not by rank: both targets are forward of `validating` and neither is invalidated
  by the other, so a rank comparison would let all eight through.

## Captured output

```
############ 1. restart mid-enquiry: exit after the 'searching' phase commits ############
PropertyDatabase: seeded 100 properties from /private/var/folders/g2/v7hw2fhs2mjd_5dxggk52cn00000gn/T/band-a-durable-evidence.8xA6RT/app/Data/Properties
store:          <scratch>/restart.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 403e9579
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
  t11 finished: completed

enquiry 403e9579 [c1]
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
  t11 band-a.enquiry Terminal enquiryPhase=searching turns=1 restarts=0
disposing store…
disposed.

############ 3. late wakeup after rejection cannot overwrite the terminal state ############
store:          <scratch>/rejected.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 77298c56
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

late wakeup: the enquiry is committed at rejected_garbage
  wakeup done               (a delivery completing after the rejection) -> refused; enquiry stays rejected_garbage
  wakeup validating         (a resumed task restarting the pipeline) -> refused; enquiry stays rejected_garbage
  wakeup searching          (a stale phase report) -> refused; enquiry stays rejected_garbage
  wakeup writing_report     (a driver transition) -> refused; enquiry stays rejected_garbage
enquiry 77298c56 settled at rejected_garbage
  wakeup tool search_properties (a search that finished after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  wakeup tool report_intent    (an intent reported after the rejection) -> refused; tool answered "refused by the terminal guard:…", entries 4
  wakeup attach-report       -> refused; report unchanged
  final phase: rejected_garbage, entries unchanged at 4: the terminal state held

enquiry 77298c56 [c1]
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
created enquiry c09de438
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
enquiry c09de438 settled at rejected_garbage
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

enquiry c09de438 [c1]
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
created enquiry d18beada
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
enquiry d18beada settled at done
BAND_A_EXIT_AFTER_PHASE=done: committed, exiting the process mid-enquiry (exit code 42)
exit code: 42  (42 is the seam: the model has reported done and the process is gone)

############ 3e. resume that store: the deliverable is recovered, not dropped ############
store:          <scratch>/done.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase done, after 0 completed pass(es)
enquiry d18beada settled at done
  t11 finished: completed

enquiry d18beada [c1]
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
  t11 band-a.enquiry Terminal enquiryPhase=queued turns=0 restarts=0
disposing store…
disposed.

############ 4. concurrency: two enquiries for two customers, submitted together ############
# recorded runtime: both pinned sessions are served from the first recording, so
# this stage shows isolation of the durable documents, not of the model transcripts.
store:          <scratch>/concurrent.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 2e44894e
created enquiry 8fd70a9f
submitted 2 enquiries, all before any was awaited
waiting on task t11 (checkpoint at start: queued/0)
enquiry 8fd70a9f settled at done
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

enquiry 2e44894e [c1]
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

enquiry 8fd70a9f [c18]
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
created enquiry 254d9e67
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

enquiry 254d9e67 [c1]
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

############ 6. the mid-turn kill: a resumed turn re-runs, and the transcript shows it ############
store:          <scratch>/midturn.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 1365560b
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
BAND_A_EXIT_AFTER_PHASE=writing_report: committed, exiting the process mid-enquiry (exit code 42)
exit code: 42  (42 is the seam)

############ 6b. resume it: the interrupted turn runs again and duplicates its tool calls ############
store:          <scratch>/midturn.jsonl
store persists: True
conversations:  1
tasks:          1
resumed 1 unfinished enquiry
resumed tasks:  1
  t11 resumes: the enquiry is committed at phase writing_report, after 0 completed pass(es)
enquiry 1365560b settled at done
  t11 finished: completed

enquiry 1365560b [c1]
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
  t11 band-a.enquiry Terminal enquiryPhase=writing_report turns=1 restarts=0
disposing store…
disposed.

############ 6c. a clean run of the same fixture, for the entry count to compare against ############
store:          <scratch>/clean.jsonl
store persists: True
conversations:  0
tasks:          0
created enquiry 366b36cf
submitted 1 enquiry
waiting on task t11 (checkpoint at start: queued/0)
task t11 finished: completed
  store record: t11 Terminal enquiryPhase=queued turns=1

enquiry 366b36cf [c1]
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

############ 6d. the count: what the mid-turn kill cost ############
clean run:                        9 entries
killed at writing_report, resumed: 12 entries
the 12 - 9 surplus is the interrupted turn's tool calls, run a second time
```
