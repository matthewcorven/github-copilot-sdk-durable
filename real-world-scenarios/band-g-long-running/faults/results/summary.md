## Fault matrix — outcome per fault per variant

| Fault | Records | baseline | durable | extension |
| --- | --- | --- | --- | --- |
| `kill-during-wait` | whether the wait survives | **lost** | **wait survived** | **wait survived** |
| `restart-after-checkpoint` | resume is exact, model calls not repeated | **resumed, completed work re-done** | **resumed, no calls repeated** | **resumed** |
| `duplicate-approval` | the decision is applied once | **doubled** | **second delivery refused** | **second delivery did not run the next step twice** |
| `late-approval` | the terminal state is not overwritten | **applied again** | **terminal state held** | **terminal state held** |
| `abort-mid-wait` | children cancelled, state final | **not exercised** | **cancelled** | **cancelled** |
| `store-outage` | no half-committed decision | **failed safely, gate still answerable** | **failed safely** | **lost with the scheduler** |

## Model calls per process, as measured

| Fault | Variant | Calls |
| --- | --- | --- |
| `kill-during-wait` | baseline | calls, faulted process: 1; exit, resume process: 134; checkpoints after: 0; gate checkpoint after: (absent) |
| `kill-during-wait` | durable | calls before the kill: 3; calls, resume process: 3; calls after finishing: 4; run at the end: terminal |
| `kill-during-wait` | extension | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; status_at_gate: Running; status_after_kill: Running; status_af… |
| `restart-after-checkpoint` | baseline | calls, process 1: 2; calls, second process: 2; calls, stopped process: 2 |
| `restart-after-checkpoint` | durable | calls, process 1: 3; calls, process 2: 3; calls added by the resume: 0; memo hits: 1; steps in the ledger after the resume: plan,research,write |
| `restart-after-checkpoint` | extension | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; status_at_gate: Running; status_after_restart: Running; answer… |
| `duplicate-approval` | baseline | calls at the gate: 2; calls, first delivery: 2; calls, second delivery: 2; second delivery approved: 1; checkpoints after the first delivery: 2; checkpoints af… |
| `duplicate-approval` | durable | calls at the gate: 3; calls, end: 4; decision after the first delivery: alice approved; decision after the second delivery: alice approved; refused by the term… |
| `duplicate-approval` | extension | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; second_raises: 1; raised_events: ManagerApproval; completed_st… |
| `late-approval` | baseline | calls, on-time process: 2; calls, late process: 2; late approval re-ran the run: 1; exit, on-time process: 0; exit, late process: 0; checkpoints after the on-t… |
| `late-approval` | durable | calls at the gate: 3; calls after the deadline: 3; gate window (s): 3; gate verdict after the deadline: timed-out; decision after the late approval: (absent); … |
| `late-approval` | extension | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; status_at_gate: Running; completed_steps_at_gate: 1; completed… |
| `abort-mid-wait` | baseline | reason checked by: BaselineCheckpoint with an unknown mode answers: Use 'run' or 'resume' |
| `abort-mid-wait` | durable | calls at the gate: 3; calls after the abort: 3; gate verdict after the abort: aborted; workflow verdict after the abort: aborted; children, as the abort handle… |
| `abort-mid-wait` | extension | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; status_at_gate: Running; completed_steps_at_gate: 1; completed… |
| `store-outage` | baseline | calls, process blocked by the fault: 2; exit, faulted process: 134; checkpoints at the gate: 1; checkpoints after: 2; post-approval checkpoint after: yes; gate… |
| `store-outage` | durable | calls at the gate: 3; calls after the outage: 3; decision after the outage: alice approved; run after the outage: waiting; verdict after the outage: (absent); … |
| `store-outage` | extension | exit, faulted process: 1; scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719; status_at_gate: Running; status_afte… |

## What each result observed

### `kill-during-wait`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **lost** | the manager's first turn, held; process killed before any gate checkp… | calls, faulted process: 1; exit, resume process: 134; check… | The kill landed on pid 5568 (the process working out the plan, inside its manager turn, before any gate checkpoint exists) with 1 model call(s) served to that process and no gate checkpoint on disk. Nothing continues it: the baseline's own continuation entry point — a fresh process running 'resume' against the directory the killed process left — exited 134 and wrote nothing, because there is no gate record (0 checkpoint file(s) on disk). The wait existed only inside the killed process, so killing it cost the whole pre-gate run. |
| durable | **wait survived** | blocked in the wait for the gate's verdict; process killed, wait stil… | calls before the kill: 3; calls, resume process: 3; calls a… | The kill landed on pid 6739 (the host process blocked in the wait for the gate's verdict) with the store reading: task waiting, gate phase wait. A fresh process re-entered the run and returned at the gate again — task waiting, gate phase wait, 3 call(s), unchanged from 3 — so the wait survived the death of the only process that was in it. It then accepted a decision (alice approved) and finished the run: terminal with verdict approved, 4 call(s). The wait is a checkpoint the gate committed, not a call the killed process held. |
| extension | **wait survived** | parked at ManagerApproval, with a process serving the answer | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b… | The run was parked at ManagerApproval with task status Running and one answer raised (1 raise(s) recorded before the kill). pid 12040 — the process carrying the worker — was killed with SIGKILL and confirmed gone; Running it was still Running. A different process then raised the port's answer and reported 'delivered' 1 time(s), and the run's history after that is: OrchestratorStarted > ExecutionStarted > TaskScheduled > OrchestratorStarted > TaskCompleted > OrchestratorStarted > EventRaised > TaskScheduled > OrchestratorStarted > TaskCompleted. Raised events now: ManagerApproval. The wait survived the death of the only process that was in it, because the wait is an orchestration instance the scheduler holds, not a call the killed process held. |

### `restart-after-checkpoint`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **resumed, completed work re-done** | stopped inside the writer's turn; a fresh process completed the run | calls, process 1: 2; calls, second process: 2; calls, stopp… | The stop landed on pid 5771 (the resumed process, inside the writer's model call, after the plan and research turns) with 2 model call(s) already served to that process. A fresh resume against the same checkpoint directory exited 0 and wrote checkpoint 52114762de054d649d9f11391bf8d4bc (2 file(s) now, approved checkpoint present: yes). The second process issued 2 model call(s): the turns the stopped process had already completed inside its own process were not carried over by its checkpoint, only the workflow's own state was, so the work of the stopped process was re-done. The model is the recorded transcript, so the count is of calls made, not of answers invented. |
| durable | **resumed, no calls repeated** | stopped after the write checkpoint (inside the write step, after its … | calls, process 1: 3; calls, process 2: 3; calls added by th… | The fault left the process before the write step's checkpoint committed, with the run at phase write and the ledger reading 3 call(s) [plan,research,write]. A fresh process re-entered the run and stopped at the gate again: task waiting, gate phase wait, 3 call(s) [plan,research,write] with 1 memo hit(s). So the resume added no model call at all for a step the store already held: every step it re-entered was served from the memo, and the resume's own count is 0. |
| extension | **resumed** | parked at ManagerApproval after the pre-gate work; a fresh process co… | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b… | The first process started the orchestration, reached ManagerApproval and exited with the run Running; its history was: OrchestratorStarted > ExecutionStarted > TaskScheduled > OrchestratorStarted > TaskCompleted. A fresh process — its own host and its own worker, no state from the first — raised the port's answer and reported 'delivered' 1 time(s). Afterwards the run was Running with history: OrchestratorStarted > ExecutionStarted > TaskScheduled > OrchestratorStarted > TaskCompleted > OrchestratorStarted > EventRaised > TaskScheduled > OrchestratorStarted > TaskCompleted; raised events: ManagerApproval. The restart continued the run rather than repeating it: the history holds 2 task completion(s) — the same single pre-gate step, not a second copy of it — and the raised events are ManagerApproval. What this column cannot state is a model-call count: the vendored sample's executors call no model, so the band's call-count comparison has no measurement here, and none is invented. |

### `duplicate-approval`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **doubled** | two resume processes against one gate checkpoint | calls at the gate: 2; calls, first delivery: 2; calls, seco… | The first resume approved and exited 0 (2 model call(s)). The second resume approved the re-emitted request again — it printed 'Approving plan' — and exited 0 after 2 model call(s), leaving the latest checkpoint at 12e8ce76a80241f9a3bec738aef9be22 (3 file(s)). The approval applied twice. |
| durable | **second delivery refused** | two approve processes against a live gate | calls at the gate: 3; calls, end: 4; decision after the fir… | alice's approval was recorded (exit 0). mallory's approval of the same live gate was refused — 'ignored: a decision is already on file (alice, approved)' — and the decision on file was still 'alice approved'. The refusal did not come from the terminal guard (the run was still 'waiting'), so it came from the decision document's first-writer-wins rule: the second delivery found a value already on file and returned it. |
| extension | **second delivery did not run the next step twice** | two answers raised for the same port | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b… | The first answer was raised and delivered; a second answer for the same port was then raised (1 raise(s) recorded). Afterwards the run had completed 2 step(s) against 1 before the answers, the raised events were ManagerApproval, and the orchestration had scheduled 2 task(s). The second event is recorded — it is a message, and the scheduler accepted it — and the completed-step count is the measurable that shows it ran nothing: the post-approval work happened once. |

### `late-approval`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **applied again** | approved after the run had already finished | calls, on-time process: 2; calls, late process: 2; late app… | The run finished on the first resume (checkpoint 7b49fe0e767841fda1014ad0e7d10916, 2 model call(s)). The later resume — the same approval, in a new process against the same checkpoint directory — approved again: it printed 'Approving plan', issued 2 model call(s) and wrote checkpoint 02361b164c1c49aba1021ec391717789. There is no terminal state to overwrite, because the baseline has no terminal record and no guard: the gate checkpoint stays answerable and a late approval re-runs the workflow from it. What the baseline also does not have is any record that this was late — no decision document and nothing that refused. |
| durable | **terminal state held** | the gate's recorded 3s deadline passed in a process that did not set … | calls at the gate: 3; calls after the deadline: 3; gate win… | The run was created with a 3-second window and its gate recorded deadline 2026-11-05T20:08:37.896336+00:00. A process started after that deadline re-entered the run: the gate woke at once and recorded 'timed-out', and the workflow concluded on it — task terminal, outcome failed, 'the plan was not approved (timed-out): no decision before the gate's deadline'. The late approval was then refused — 'refused: the workflow is Terminal, so this decision is late' — and the decision document is still absent. The terminal state was not overwritten, and the refusal names the terminal record rather than the document rule, which is the different guard a late approval meets. |
| extension | **terminal state held** | terminate acknowledged, then answered by a live worker | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b… | The run was parked at ManagerApproval (1 completed step(s)). The terminate was acknowledged (1 acknowledgement(s)). The answer was then raised with a worker present ('delivered' printed 0 time(s), 1 raise(s) recorded); the run is Terminated with 1 completed step(s) against 1 at the gate, an ExecutionTerminated in its history: 1, and history: OrchestratorStarted > ExecutionStarted > TaskScheduled > OrchestratorStarted > TaskCompleted > OrchestratorStarted > ExecutionTerminated > EventRaised > ExecutionCompleted > TaskScheduled. No step beyond the gate's ran, so the post-approval step did not run on a terminated run. The worker was present, so a silent orchestrator is not an alternative explanation for that. |

### `abort-mid-wait`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **not exercised** | a run waiting at the gate, with an abort to inject | reason checked by: BaselineCheckpoint with an unknown mode … | The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. So there is no abort verb and no abort tree over the workflow's pending work: the wait is a pending request inside a checkpoint, and the only operations on it are to answer it and continue. Injecting an abort would mean adding one to the baseline, which this band does not do. Recorded as not exercised with that reason rather than reported as a tie or a loss. |
| durable | **cancelled** | waiting at the gate (gate phase wait); abort called | calls at the gate: 3; calls after the abort: 3; gate verdic… | Before the abort: task waiting, gate phase wait (running), 3 call(s). The abort exited 0 and both tasks ended terminal — the gate 'aborted', the workflow 'aborted'. The parent's abort handler recorded what it saw of its children when it ran: 't59=Terminal:aborted'. The child was already terminal at that moment, which is the cascade shown as an ordering and not merely as two terminal states. A later approval was then refused — 'refused: the workflow is Terminal, so this decision is late' — the decision document is '(absent)', and the run is still terminal with verdict 'aborted'. |
| extension | **cancelled** | parked at ManagerApproval, terminated | scheduler_image_digest: sha256:82ceb7df2fdfc2e22b0013c7b87b… | The run was parked at ManagerApproval (Running, 1 completed step(s)). The terminate was acknowledged (1 acknowledgement(s)) and the runtime status sampled every 3s afterwards was: Running Running Running Running Running, ending Running. A later answer was delivered by a live worker ('delivered' printed 0 time(s)); the run ended Terminated with history: OrchestratorStarted > ExecutionStarted > TaskScheduled > OrchestratorStarted > TaskCompleted > OrchestratorStarted > ExecutionTerminated > EventRaised > ExecutionCompleted > TaskScheduled — 1 completed step(s) before the answer and 1 after, and an ExecutionTerminated in the history. So the orchestration stopped and the answer did not run the step after the port. Scope: this column has no separate child work to cancel — the ports live inside one orchestration — so 'children cancelled' is not observable here and is not claimed. |

### `store-outage`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **failed safely, gate still answerable** | the checkpoint directory made immutable while a resume was committing | calls, process blocked by the fault: 2; exit, faulted proce… | The store — the checkpoint directory itself — was made immutable with chflags uchg while a resume process was writing its post-approval checkpoint. That process exited 134 and its own output says why: the framework could not create a checkpoint. So the commit it was making did not happen and no partial checkpoint was left — the directory held 1 checkpoint file(s) at the gate. What is left is the gate checkpoint, still holding the plan-review request (16dee0365a914b4898652e0bdc07483c): with the flag removed, a fresh resume exited 0, answered the request again (1 re-approvals), wrote checkpoint 51b244d8149946438fe9cab0ba27317e, and left a post-approval checkpoint: yes. That is a clean failure and also a limit: the baseline's gate has no way to say it has already been answered, so the same approval is still there to be applied a second time. |
| durable | **failed safely** | the store made immutable while the run drove a recorded decision | calls at the gate: 3; calls after the outage: 3; decision a… | The decision was on file before the outage ('alice approved') and the store file was made immutable with chflags uchg. The resume that would have applied it exited 70, so the commit that records the approved run did not happen. With the flag removed, the store still reads: the decision is still 'alice approved', the run is 'waiting' with verdict '(absent)', the ledger is unchanged at 3 call(s), the file is 31902 bytes before and 31902 after, and a fresh 'inspect' exited 0. The decision and the run agree, and the run did not advance on a write that failed. |
| extension | **lost with the scheduler** | the scheduler stopped and restarted under a live run | exit, faulted process: 1; scheduler_image_digest: sha256:82… | The run was parked at ManagerApproval (Running) when the Durable Task Scheduler was stopped. The process trying to answer it exited 1. With the scheduler restarted, the instance reads '(absent)' with history '(empty)' and raised '(none)': the run did not survive the scheduler. On this substrate the scheduler is not just the transport, it is the state — a stopped scheduler is a lost run, and the emulator holds its state in memory. Narrowed to what was measured: this says what *this emulator* does, not what a production scheduler does. |

## Before/after controls

Each control runs the same steps with the injection removed, so the pair differs only in whether
the fault happened. `abort-mid-wait` has no control: the control for an abort is a run that was
never aborted, which the band's own two-process evidence already covers — and its result says so.

| Fault | Variant | Control outcome | Fault outcome |
| --- | --- | --- | --- |
| `kill-during-wait` | baseline | reached-the-gate | lost |
| `kill-during-wait` | durable | no kill, run completed | wait survived |
| `kill-during-wait` | extension | no kill, run continued | wait survived |
| `restart-after-checkpoint` | baseline | resumed, uninterrupted | resumed, completed work re-done |
| `restart-after-checkpoint` | durable | resumed, uninterrupted | resumed, no calls repeated |
| `restart-after-checkpoint` | extension | resumed | resumed |
| `duplicate-approval` | baseline | applied once | doubled |
| `duplicate-approval` | durable | one delivery, nothing to refuse | second delivery refused |
| `duplicate-approval` | extension | one delivery | second delivery did not run the next step twice |
| `late-approval` | baseline | approved in time | applied again |
| `late-approval` | durable | approved in time | terminal state held |
| `late-approval` | extension | no terminate, answer advanced the run | terminal state held |
| `abort-mid-wait` | extension | no abort, run advanced | cancelled |
| `store-outage` | baseline | completed | failed safely, gate still answerable |
| `store-outage` | durable | completed | failed safely |
| `store-outage` | extension | completed | lost with the scheduler |

## Not exercised, and why

- `baseline` / `abort-mid-wait`: The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. So there is no abort verb and no abort tree over the workflow's pending work: the wait is a pending request inside a checkpoint, and the only operations on it are to answer it and continue. Injecting an abort would mean adding one to the baseline, which this band does not do. Recorded as not exercised with that reason rather than reported as a tie or a loss.

## Harness status

- `baseline` kill-during-wait: exit 0
- `baseline` kill-during-wait-control: exit 0
- `baseline` restart-after-checkpoint: exit 0
- `baseline` restart-after-checkpoint-control: exit 0
- `baseline` duplicate-approval: exit 0
- `baseline` duplicate-approval-control: exit 0
- `baseline` late-approval: exit 0
- `baseline` late-approval-control: exit 0
- `baseline` abort-mid-wait: exit 0
- `baseline` store-outage: exit 0
- `baseline` store-outage-control: exit 0
- `durable` kill-during-wait: exit 0
- `durable` kill-during-wait-control: exit 0
- `durable` restart-after-checkpoint: exit 0
- `durable` restart-after-checkpoint-control: exit 0
- `durable` duplicate-approval: exit 0
- `durable` duplicate-approval-control: exit 0
- `durable` late-approval: exit 0
- `durable` late-approval-control: exit 0
- `durable` abort-mid-wait: exit 0
- `durable` store-outage: exit 0
- `durable` store-outage-control: exit 0
- `extension` kill-during-wait: exit 0
- `extension` kill-during-wait-control: exit 0
- `extension` restart-after-checkpoint: exit 0
- `extension` restart-after-checkpoint-control: exit 0
- `extension` duplicate-approval: exit 0
- `extension` duplicate-approval-control: exit 0
- `extension` late-approval: exit 0
- `extension` late-approval-control: exit 0
- `extension` abort-mid-wait: exit 0
- `extension` abort-mid-wait-control: exit 0
- `extension` store-outage: exit 0
- `extension` store-outage-control: exit 0


## The band's three-way comparison, read from these results

All three columns were run. Where a cell could not be measured in its column, the cell says so and
does not borrow a number from another one.

| | File checkpoint (baseline, #176) | Durable Task extension on DTS (`extension/`) | This variant (#177) |
| --- | --- | --- | --- |
| **Restart/resume** | **Yes, measured.** `kill-during-wait`: lost; `restart-after-checkpoint`: resumed, completed work re-done after 2 calls in the stopped process. | **Yes, measured.** `kill-during-wait`: wait survived; `restart-after-checkpoint`: resumed — a fresh process, its own worker, finished the parked run (status after: Running). | **Yes, measured.** `kill-during-wait`: wait survived; `restart-after-checkpoint`: resumed, no calls repeated. |
| **Re-does model calls on resume** | **Yes, measured.** The stopped resumed process made 2 calls; the fresh resume made 2 more for the same turns. | **Not measurable here.** The vendored sample's executors call no model, so there is no call to count. The band's call-count comparison therefore has no number for this column, and none is borrowed. | **No, measured.** The resume added **0** calls and served the re-entered step from the memo (1 memo hit(s)). |
| **Domain state transactional** | **No.** The checkpoint is the workflow's state; a completed turn inside a process is not in it, which is what the call count above shows. | **In the substrate, and only as durable as it.** Stopping the process mid-run lost nothing (`kill-during-wait`: wait survived). Stopping the *scheduler* lost the run: `store-outage`: lost with the scheduler — the instance did not exist after a restart. State is the scheduler's, and on the emulator that also means it is the emulator's lifetime. | **Within the run.** A step's checkpoint, its memo and its ledger entry commit together: `store-outage` refused the commit (exit 70), left the run 'waiting' with the decision still 'alice approved', and the store file identical in size before and after (31902 -> 31902 bytes): a refused commit left no partial write. The exact byte count is run-variable; its equality is not. |
| **What a decision is** | **A request/response pair, not a value.** `duplicate-approval`: doubled — a second resume approves again. `late-approval`: applied again — the finished gate checkpoint is still answerable. | **An event on the orchestration instance.** `duplicate-approval`: second delivery did not run the next step twice — the second event is recorded but schedules no second step. `late-approval`: terminal state held. | **A first-writer-wins document.** `duplicate-approval`: second delivery refused, and the refusal did not come from the terminal guard (0). `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **A decision after a terminal state** | **Applied again.** `late-approval`: applied again. There is no terminal record to refuse it. | **Not moved.** `late-approval`: terminal state held — a late answer delivered by a live worker completed 1 step(s) against 1 at the gate. | **Held.** `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **Abort mid-wait** | **Not exercised.** The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. | **Stops, with no tree to cascade.** `abort-mid-wait`: cancelled — the run was terminated and an answer delivered by a live worker completed 1 step(s) against 1 at the gate, i.e. the step after the port did not run. There is no child work: the ports live inside one orchestration. | **Cascades, measured.** cancelled — the parent's handler saw the child already terminal (t59=Terminal:aborted). |
| **Operational weight** | A directory on disk, one writer (the framework's store refuses a second). | **The heaviest, and the least forgiving.** A scheduler (or its emulator) is a prerequisite even for one run. Stopping it under a live run did not fail over: the run was gone when the scheduler came back (`store-outage`: lost with the scheduler). That is the cost the other two columns do not have — their store is a file this harness can move and restore. | A store file, one writer. Measured only in that shape: no server, no scheduler. |
