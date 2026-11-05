## Fault matrix — outcome per fault per variant

| Fault | Records | baseline | durable |
| --- | --- | --- | --- |
| `kill-during-wait` | whether the wait survives | **lost** | **wait survived** |
| `restart-after-checkpoint` | resume is exact, model calls not repeated | **resumed, completed work re-done** | **resumed, no calls repeated** |
| `duplicate-approval` | the decision is applied once | **doubled** | **second delivery refused** |
| `late-approval` | the terminal state is not overwritten | **applied again** | **terminal state held** |
| `abort-mid-wait` | children cancelled, state final | **not exercised** | **cancelled** |
| `store-outage` | no half-committed decision | **failed safely, gate still answerable** | **failed safely** |

## Model calls per process, as measured

| Fault | Variant | Calls |
| --- | --- | --- |
| `kill-during-wait` | baseline | calls, faulted process: 1; exit, resume process: 134; checkpoints after: 0; gate checkpoint after: (absent) |
| `kill-during-wait` | durable | calls before the kill: 3; calls, resume process: 3; calls after finishing: 4; run at the end: terminal |
| `restart-after-checkpoint` | baseline | calls, process 1: 2; calls, second process: 2; calls, stopped process: 2 |
| `restart-after-checkpoint` | durable | calls, process 1: 3; calls, process 2: 3; calls added by the resume: 0; memo hits: 1; steps in the ledger after the resume: plan,research,write |
| `duplicate-approval` | baseline | calls at the gate: 2; calls, first delivery: 2; calls, second delivery: 2; second delivery approved: 1; checkpoints after the first delivery: 2; checkpoints af… |
| `duplicate-approval` | durable | calls at the gate: 3; calls, end: 4; decision after the first delivery: alice approved; decision after the second delivery: alice approved; refused by the term… |
| `late-approval` | baseline | calls, on-time process: 2; calls, late process: 2; late approval re-ran the run: 1; exit, on-time process: 0; exit, late process: 0; checkpoints after the on-t… |
| `late-approval` | durable | calls at the gate: 3; calls after the deadline: 3; gate window (s): 3; gate verdict after the deadline: timed-out; decision after the late approval: (absent); … |
| `abort-mid-wait` | baseline | reason checked by: BaselineCheckpoint with an unknown mode answers: Use 'run' or 'resume' |
| `abort-mid-wait` | durable | calls at the gate: 3; calls after the abort: 3; gate verdict after the abort: aborted; workflow verdict after the abort: aborted; children, as the abort handle… |
| `store-outage` | baseline | calls, process blocked by the fault: 2; exit, faulted process: 134; checkpoints at the gate: 1; checkpoints after: 2; post-approval checkpoint after: yes; gate… |
| `store-outage` | durable | calls at the gate: 3; calls after the outage: 3; decision after the outage: alice approved; run after the outage: waiting; verdict after the outage: (absent); … |

## What each result observed

### `kill-during-wait`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **lost** | the manager's first turn, held; process killed before any gate checkp… | calls, faulted process: 1; exit, resume process: 134; check… | The kill landed on pid 497 (the process working out the plan, inside its manager turn, before any gate checkpoint exists) with 1 model call(s) served to that process and no gate checkpoint on disk. Nothing continues it: the baseline's own continuation entry point — a fresh process running 'resume' against the directory the killed process left — exited 134 and wrote nothing, because there is no gate record (0 checkpoint file(s) on disk). The wait existed only inside the killed process, so killing it cost the whole pre-gate run. |
| durable | **wait survived** | blocked in the wait for the gate's verdict; process killed, wait stil… | calls before the kill: 3; calls, resume process: 3; calls a… | The kill landed on pid 3111 (the host process blocked in the wait for the gate's verdict) with the store reading: task waiting, gate phase wait. A fresh process re-entered the run and returned at the gate again — task waiting, gate phase wait, 3 call(s), unchanged from 3 — so the wait survived the death of the only process that was in it. It then accepted a decision (alice approved) and finished the run: terminal with verdict approved, 4 call(s). The wait is a checkpoint the gate committed, not a call the killed process held. |

### `restart-after-checkpoint`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **resumed, completed work re-done** | stopped inside the writer's turn; a fresh process completed the run | calls, process 1: 2; calls, second process: 2; calls, stopp… | The stop landed on pid 1012 (the resumed process, inside the writer's model call, after the plan and research turns) with 2 model call(s) already served to that process. A fresh resume against the same checkpoint directory exited 0 and wrote checkpoint 35f6167be6134ee2b97ac1a60266384a (2 file(s) now, approved checkpoint present: yes). The second process issued 2 model call(s): the turns the stopped process had already completed inside its own process were not carried over by its checkpoint, only the workflow's own state was, so the work of the stopped process was re-done. The model is the recorded transcript, so the count is of calls made, not of answers invented. |
| durable | **resumed, no calls repeated** | stopped after the write checkpoint (inside the write step, after its … | calls, process 1: 3; calls, process 2: 3; calls added by th… | The fault left the process before the write step's checkpoint committed, with the run at phase write and the ledger reading 3 call(s) [plan,research,write]. A fresh process re-entered the run and stopped at the gate again: task waiting, gate phase wait, 3 call(s) [plan,research,write] with 1 memo hit(s). So the resume added no model call at all for a step the store already held: every step it re-entered was served from the memo, and the resume's own count is 0. |

### `duplicate-approval`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **doubled** | two resume processes against one gate checkpoint | calls at the gate: 2; calls, first delivery: 2; calls, seco… | The first resume approved and exited 0 (2 model call(s)). The second resume approved the re-emitted request again — it printed 'Approving plan' — and exited 0 after 2 model call(s), leaving the latest checkpoint at a4d4f61721444ef19ba415919a8e472c (3 file(s)). The approval applied twice. |
| durable | **second delivery refused** | two approve processes against a live gate | calls at the gate: 3; calls, end: 4; decision after the fir… | alice's approval was recorded (exit 0). mallory's approval of the same live gate was refused — 'ignored: a decision is already on file (alice, approved)' — and the decision on file was still 'alice approved'. The refusal did not come from the terminal guard (the run was still 'waiting'), so it came from the decision document's first-writer-wins rule: the second delivery found a value already on file and returned it. |

### `late-approval`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **applied again** | approved after the run had already finished | calls, on-time process: 2; calls, late process: 2; late app… | The run finished on the first resume (checkpoint a39ab156ca724762817ba6f164be00bf, 2 model call(s)). The later resume — the same approval, in a new process against the same checkpoint directory — approved again: it printed 'Approving plan', issued 2 model call(s) and wrote checkpoint 81b9a692b0744603a3e5e607f7e68350. There is no terminal state to overwrite, because the baseline has no terminal record and no guard: the gate checkpoint stays answerable and a late approval re-runs the workflow from it. What the baseline also does not have is any record that this was late — no decision document and nothing that refused. |
| durable | **terminal state held** | the gate's recorded 3s deadline passed in a process that did not set … | calls at the gate: 3; calls after the deadline: 3; gate win… | The run was created with a 3-second window and its gate recorded deadline 2026-11-05T18:37:59.077857+00:00. A process started after that deadline re-entered the run: the gate woke at once and recorded 'timed-out', and the workflow concluded on it — task terminal, outcome failed, 'the plan was not approved (timed-out): no decision before the gate's deadline'. The late approval was then refused — 'refused: the workflow is Terminal, so this decision is late' — and the decision document is still absent. The terminal state was not overwritten, and the refusal names the terminal record rather than the document rule, which is the different guard a late approval meets. |

### `abort-mid-wait`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **not exercised** | a run waiting at the gate, with an abort to inject | reason checked by: BaselineCheckpoint with an unknown mode … | The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. So there is no abort verb and no abort tree over the workflow's pending work: the wait is a pending request inside a checkpoint, and the only operations on it are to answer it and continue. Injecting an abort would mean adding one to the baseline, which this band does not do. Recorded as not exercised with that reason rather than reported as a tie or a loss. |
| durable | **cancelled** | waiting at the gate (gate phase wait); abort called | calls at the gate: 3; calls after the abort: 3; gate verdic… | Before the abort: task waiting, gate phase wait (running), 3 call(s). The abort exited 0 and both tasks ended terminal — the gate 'aborted', the workflow 'aborted'. The parent's abort handler recorded what it saw of its children when it ran: 't59=Terminal:aborted'. The child was already terminal at that moment, which is the cascade shown as an ordering and not merely as two terminal states. A later approval was then refused — 'refused: the workflow is Terminal, so this decision is late' — the decision document is '(absent)', and the run is still terminal with verdict 'aborted'. |

### `store-outage`

| Variant | Outcome | Phase | Calls | Detail |
| --- | --- | --- | --- | --- |
| baseline | **failed safely, gate still answerable** | the checkpoint directory made immutable while a resume was committing | calls, process blocked by the fault: 2; exit, faulted proce… | The store — the checkpoint directory itself — was made immutable with chflags uchg while a resume process was writing its post-approval checkpoint. That process exited 134 and its own output says why: the framework could not create a checkpoint. So the commit it was making did not happen and no partial checkpoint was left — the directory held 1 checkpoint file(s) at the gate. What is left is the gate checkpoint, still holding the plan-review request (62c3cdc159aa430ba906e4b3ec2d097f): with the flag removed, a fresh resume exited 0, answered the request again (1 re-approvals), wrote checkpoint f2e6d316e5f44e6c967af9134d30ee2e, and left a post-approval checkpoint: yes. That is a clean failure and also a limit: the baseline's gate has no way to say it has already been answered, so the same approval is still there to be applied a second time. |
| durable | **failed safely** | the store made immutable while the run drove a recorded decision | calls at the gate: 3; calls after the outage: 3; decision a… | The decision was on file before the outage ('alice approved') and the store file was made immutable with chflags uchg. The resume that would have applied it exited 70, so the commit that records the approved run did not happen. With the flag removed, the store still reads: the decision is still 'alice approved', the run is 'waiting' with verdict '(absent)', the ledger is unchanged at 3 call(s), the file is 31901 bytes before and 31901 after, and a fresh 'inspect' exited 0. The decision and the run agree, and the run did not advance on a write that failed. |

## Before/after controls

Each control runs the same steps with the injection removed, so the pair differs only in whether
the fault happened. `abort-mid-wait` has no control: the control for an abort is a run that was
never aborted, which the band's own two-process evidence already covers — and its result says so.

| Fault | Variant | Control outcome | Fault outcome |
| --- | --- | --- | --- |
| `kill-during-wait` | baseline | reached-the-gate | lost |
| `kill-during-wait` | durable | no kill, run completed | wait survived |
| `restart-after-checkpoint` | baseline | resumed, uninterrupted | resumed, completed work re-done |
| `restart-after-checkpoint` | durable | resumed, uninterrupted | resumed, no calls repeated |
| `duplicate-approval` | baseline | applied once | doubled |
| `duplicate-approval` | durable | one delivery, nothing to refuse | second delivery refused |
| `late-approval` | baseline | approved in time | applied again |
| `late-approval` | durable | approved in time | terminal state held |
| `store-outage` | baseline | completed | failed safely, gate still answerable |
| `store-outage` | durable | completed | failed safely |

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


## The band's three-way comparison, with the measured cells read from these results

The middle column was not run. Its marking is the same one G2 gave it, and it is carried here so a
reader cannot infer a measurement that does not exist.

| | File checkpoint (baseline, #176) | Durable Task (agent-framework-durable-extension) | This variant (#177) |
| --- | --- | --- | --- |
| **Restart/resume** | **Yes, measured.** `kill-during-wait`: lost; `restart-after-checkpoint`: resumed, completed work re-done after 2 calls in the stopped process. | **Not run here.** Documented: completed steps are not repeated after a restart. The durable-extension sample was not stood up (no prerelease package on NuGet — see the band README). | **Yes, measured.** `kill-during-wait`: wait survived; `restart-after-checkpoint`: resumed, no calls repeated. |
| **Re-does model calls on resume** | **Yes, measured.** The stopped resumed process made 2 calls; the fresh resume made 2 more for the same turns, contents identical by request shape. | **Not run here.** Documented: replay does not re-run completed agent executions. | **No, measured.** The resume added **0** calls and served the re-entered step from the memo (1 memo hit(s)). |
| **Domain state transactional** | **No.** The checkpoint is the workflow's state; a completed turn inside a process is not in it, which is what the call count above shows. | **Not run here.** Documented as partial: workflow and conversation state are checkpointed, a side effect is not. | **Within the run.** A step's checkpoint, its memo and its ledger entry commit together: `store-outage` refused the commit (exit 70), left the run 'waiting' with the decision still 'alice approved', and the store file byte-identical at 31901 bytes. |
| **What a decision is** | **A request/response pair, not a value.** `duplicate-approval`: doubled — a second resume approves again. `late-approval`: applied again — the finished gate checkpoint is still answerable. | **Not run here.** An external event on an orchestration instance, with a timer for the timeout. | **A first-writer-wins document.** `duplicate-approval`: second delivery refused, and the refusal did not come from the terminal guard (0). `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **A decision after a terminal state** | **Applied again.** `late-approval`: applied again. There is no terminal record to refuse it. | **Not run here.** The runtime refuses an event raised after completion; not exercised. | **Held.** `late-approval`: terminal state held — refused: the workflow is Terminal, so this decision is late. |
| **Abort mid-wait** | **Not exercised.** The baseline's port advertises exactly two modes, 'run' and 'resume' — checked in this run, where an unknown mode answers 'Use run or resume'. | **Not run here.** Orchestration termination exists; a cascade over child work is not described. | **Cascades, measured.** cancelled — the parent's handler saw the child already terminal (t59=Terminal:aborted). |
| **Operational weight** | A directory on disk, one writer (the framework's store refuses a second). | **Not run here.** DTS + emulator, per the documented prerequisites. | A store file, one writer. Measured only in that shape: no server, no scheduler. |
