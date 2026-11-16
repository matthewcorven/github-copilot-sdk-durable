## The fault matrix — what the platform did, and what the application did

Every cell comes from a result file's own block: `platform` for the first column, `application` for the second. Nothing here is typed by hand, and no cell borrows the other's evidence.

| Fault | Variant | Platform | Application |
| --- | --- | --- | --- |
| `client-dies-mid-run` | baseline | live Foundry: **no** — not exercised in this run: no hosted deployment was reachable, so what Foundry would have… | **one step repeated after the client died** |
| `client-dies-mid-run` | durable-inside | live Foundry: **no** — not exercised in this run: this fault has no platform-side question in it, and no hosted … | **not exercised: this variant has no client process to kill** |
| `client-dies-mid-run` | durable-outside | live Foundry: **no** — not exercised in this run: no hosted deployment was reachable, so what Foundry would have… | **unfinished step re-called; completed steps not repeated** |
| `container-restart` | baseline | live Foundry: **no** — not exercised in this run: no hosted deployment existed, so the platform's session and id… | **cold start: the interrupted step was re-driven from its beginning** |
| `container-restart` | durable-inside | live Foundry: **no** — not exercised in this run: nothing was provisioned, so the platform's own session behavio… | **working state and pending approval survived the container restart** |
| `container-restart` | durable-outside | live Foundry: **no** — not exercised in this run: this fault has no platform-side question in it, and no hosted … | **not exercised: this variant's restart is the death of its client, which is client-dies-mid-run** |
| `duplicate-delivery` | baseline | live Foundry: **no** — not exercised in this run: the platform's response records are not the application's deci… | **both steps applied twice; nothing refused the second delivery** |
| `duplicate-delivery` | durable-inside | live Foundry: **no** — not exercised in this run: Foundry was not asked to deliver anything twice, and it has no… | **one decision applied once; the second delivery woke nothing** |
| `duplicate-delivery` | durable-outside | live Foundry: **no** — not exercised in this run: this fault has no platform-side question in it, and no hosted … | **not exercised: this variant's gate refuses a second answer by construction, which is a property of its gate and not a fault to inject** |
| `concurrent-clients` | baseline | live Foundry: **no** — not exercised in this run: two clients against one *platform* session is not something th… | **both clients applied both steps** |
| `concurrent-clients` | durable-inside | live Foundry: **no** — not exercised in this run: two clients against one *platform* session is not something th… | **the second client was refused by the store's lock** |
| `concurrent-clients` | durable-outside | live Foundry: **no** — not exercised in this run: two clients against one *platform* session is not something th… | **the second client was refused while the first held the store** |
| `store-outage` | baseline | live Foundry: **no** — not exercised in this run: no platform session existed, and the stand-in service is not t… | **failed loudly and recorded it; nothing was kept** |
| `store-outage` | durable-inside | live Foundry: **no** — not exercised in this run: the Durable store is the application's, and Foundry has no par… | **failed closed, and the committed state was still there afterwards** |
| `store-outage` | durable-outside | live Foundry: **no** — not exercised in this run: the store is local and Durable's, not the platform's | **the commit was refused and the run did not advance** |
| `agent-call-failure` | baseline | live Foundry: **no** — not exercised in this run: the failure came from this harness's stand-in service, and no … | **the failed call was retried; the earlier step was skipped by its result file** |
| `agent-call-failure` | durable-inside | live Foundry: **no** — not exercised in this run: the only model call in D2 is the platform's own, inside the ho… | **not exercised: this variant's application code makes no agent call** |
| `agent-call-failure` | durable-outside | live Foundry: **no** — not exercised in this run: the 503 came from lib/fake_foundry.py, this harness's stand-in… | **the failure ended the run, so the resume had nothing to re-enter** |

## The four documented platform behaviours

Each result carries all four. No fault in this directory reached Foundry, so every row is `documented, not exercised here` — quoted from D1's captures and the documentation, never reported as reproduced.

| Behaviour | Status | Source | Spoken to by |
| --- | --- | --- | --- |
| `session-persistence` | **documented, not exercised here** | learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-session… | baseline/container-restart, durable-inside/container-restart, baseline/store-outage |
| `idle-timeout` | **documented, not exercised here** | learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-session… | baseline/container-restart, durable-inside/container-restart |
| `foreground-cancelled` | **documented, not exercised here** | learn.microsoft.com/azure/foundry/agents/concepts/long-running-agent-… | baseline/client-dies-mid-run, durable-outside/client-dies-mid-run, baseline/container-restart |
| `no-replay` | **documented, not exercised here** | learn.microsoft.com/azure/foundry/agents/how-to/recover-long-running-… | baseline/client-dies-mid-run, durable-outside/client-dies-mid-run |

## What each row does and does not show

### `client-dies-mid-run`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **one step repeated after the client died** | The first client was killed inside its held coverage call (service had accepted 1 POST(s) for that agent). A new client, having no progress record beyond the step files it had not yet written, drove the run again: the service accepted 2 coverage POST(s) in total. Nothing in the baseline says 'this step was already paid for' — its only memory is the result file, which the killed process had not wr… |
| durable-inside | **not exercised: this variant has no client process to kill** | D2's turn is a platform request served inside the hosted container; the harness has no client of its own to kill, and the platform's foreground-cancellation behaviour is documented rather than exercised here. Failing to kill something would have been reported as a missing result; this says which variant and why. |
| durable-outside | **unfinished step re-called; completed steps not repeated** | The orchestrator was killed inside its held coverage call (service had accepted 1 POST(s) for that agent). The store held the task at its coverage checkpoint and no memo for the interrupted step, because the step had not returned. The second process re-entered coverage and called again: 2 coverage POST(s) in total. That is 2 call(s) for one completed step. The boundary this measures is exact: a m… |

### `container-restart`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **cold start: the interrupted step was re-driven from its beginning** | The driving client was killed with SIGKILL inside the held synthesis call (the service had accepted 1 POST(s) for it). No container exists for D1 - the platform runs the image and the client is the process - so the restart is the client's. The new client found one step result on disk and no record of the call that was in flight, and drove the run again from the first step without a result: the se… |
| durable-inside | **working state and pending approval survived the container restart** | A container committed the assessment and a parked approval (task t4), then was stopped with SIGTERM while a later step was held open. A new container over the same session volume read the approval back with the same deadline (1794927783.686035) — byte-identical, yes — and a third process delivered the decision, which woke the parked task: resume settled completed and the work state reached 'decid… |
| durable-outside | **not exercised: this variant's restart is the death of its client, which is client-dies-mid-run** | D3's state is a local store and its process is the run. Stopping it is a client death (client-dies-mid-run); there is no container of its own to restart, and pretending otherwise would have measured the harness's own process. |

### `duplicate-delivery`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **both steps applied twice; nothing refused the second delivery** | The same request was driven twice through the driver's own entry point. The first run made 1 coverage call(s) and 1 synthesis call(s); the second run made the total 2 and 2. The driver's only record of progress is a result file per step, and its resume path skips a step that has one — but a *fresh* delivery does not consult it, which is the shape a retried request has. No refusal was recorded bec… |
| durable-inside | **one decision applied once; the second delivery woke nothing** | The approval was delivered twice from two processes: 'approved by Dr Chen' first, then 'denied by Dr Mallory'. The first delivery woke 1 task(s); the second woke 0, because the task was no longer waiting on the event and the answer memo was already set. A third process resumed the task: it settled, and the decision on file is 'approved' by 'Dr Chen' — the first delivery's, not the second's. The a… |
| durable-outside | **not exercised: this variant's gate refuses a second answer by construction, which is a property of its gate and not a fault to inject** | gate.sh refuses to overwrite an existing decision, so the duplicate is refused before any state is touched. That is worth knowing and is not a fault injection: the observation would be about the harness's own file-write guard. |

### `concurrent-clients`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **both clients applied both steps** | Two driver processes were started against the one service. The service accepted 2 coverage POST(s) and 2 synthesis POST(s), for one request. Neither process refused, because the baseline has no store and therefore nothing that could see the other. A retried request on this design costs a second full run of every step. |
| durable-inside | **the second client was refused by the store's lock** | One container opened the store and held it, committing the assessment. A second container then tried the same store: its open failed with the layer's single-writer refusal ('already open in another process (pid=1 started=2026-11-16T15:05:03+0000). A store file supports a single writer at a time; close the other store before opening this one.') and it exited 1 without writing. The work state holds… |
| durable-outside | **the second client was refused while the first held the store** | Two published orchestrator processes over one store directory. The first opened the store, recorded the coverage step and reached its human gate; the second was refused by the layer's single-writer lock ('is already open in another process') and exited 134 without writing. The first then completed its gate normally (exit 0). Nothing in D3 checks whether another process is running - the lock is th… |

### `store-outage`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **failed loudly and recorded it; nothing was kept** | The baseline has no Durable store to lose, so the outage was injected in the thing its run depends on: the service it calls was stopped, and the run failed on its first call. The client exited 3 and wrote a failure record rather than a partial result. That is a fail-loudly answer, and it is *not* the same fault the Durable variants received — a stateless client keeps no state to protect, which is… |
| durable-inside | **failed closed, and the committed state was still there afterwards** | The store's volume was made read-only for the process that ran while it was unavailable, so the commit was refused by the filesystem: the process exited 1 with 'OSError: [Errno 30] Read-only file system: '/home/session/durable/working-state.jsonl.lock'' and the store's digest was unchanged. The same volume was then writable again and read back: the state the first process committed is still there… |
| durable-outside | **the commit was refused and the run did not advance** | The store file was made immutable (chflags uchg) while the run was live and waiting at its human gate. The gate was then answered. The process exited 1 with 'orchestrator: run did not complete: failed Access to the path '$FAULTS_SCRATCH/runs/durable-outside/store-outage/run/store/orchestrator.jsonl' is denied.'. After the flag was removed the store was read back: {"readable": true, "records": 3, … |

### `agent-call-failure`

| Variant | Outcome | Detail |
| --- | --- | --- |
| baseline | **the failed call was retried; the earlier step was skipped by its result file** | The synthesis agent answered 503 for every attempt, so the run failed (exit 3) after coverage had succeeded. The failure was lifted and the client resumed: coverage was called 0 more time(s) and synthesis 1 more time(s), and the run finished: yes. The skip is real and it is a *file check*, not a memo: there is no key over the step or the agent's identity, so a fresh client delivering the same req… |
| durable-inside | **not exercised: this variant's application code makes no agent call** | D2's application code (agent/durable_state.py, agent/d2_tool.py) writes the working state and parks an approval task. The agent call that produces the assessment is the platform's, made by the hosted turn before the tool runs. The D3 memo this fault is about has no counterpart here, so there is nothing to fail and nothing to memoise. |
| durable-outside | **the failure ended the run, so the resume had nothing to re-enter** | The decision-letter agent answered 503 for every attempt (the orchestrator retries 4x), and the failure settled the task as failed rather than leaving it running: the first process exited 1, and its store is at phase 'new run (no checkpoint before this process)' with 4 letter call attempt(s). 'already finished' in the resume log is the reason nothing was called again: 'memos:      2 on record'. C… |

## Before/after controls

Each control runs the same steps with the injection removed, so a difference between the pair belongs to the fault. A control is named `control: …` in its own outcome so it is never read as the fault's.

| Fault | Variant | Control outcome | Fault outcome |
| --- | --- | --- | --- |
| `client-dies-mid-run` | baseline | control: completed in one client | one step repeated after the client died |
| `client-dies-mid-run` | durable-inside | not exercised: control for a fault this variant cannot have | not exercised: this variant has no client process to kill |
| `client-dies-mid-run` | durable-outside | control: completed in one client | unfinished step re-called; completed steps not repeated |
| `container-restart` | baseline | control: completed without a restart | cold start: the interrupted step was re-driven from its beginning |
| `container-restart` | durable-inside | control: completed without a restart | working state and pending approval survived the container restart |
| `container-restart` | durable-outside | not exercised: control for a fault this variant cannot have | not exercised: this variant's restart is the death of its client, which is client-dies-mid-run |
| `duplicate-delivery` | baseline | control: one delivery, one application of each step | both steps applied twice; nothing refused the second delivery |
| `duplicate-delivery` | durable-inside | control: one delivery, one decision | one decision applied once; the second delivery woke nothing |
| `duplicate-delivery` | durable-outside | not exercised: control for a fault this variant cannot have | not exercised: this variant's gate refuses a second answer by construction, which is a property of its gate and not a fault to inject |
| `concurrent-clients` | baseline | control: both clients ran, and nothing overlapped | both clients applied both steps |
| `concurrent-clients` | durable-inside | control: both clients ran, and nothing overlapped | the second client was refused by the store's lock |
| `concurrent-clients` | durable-outside | control: both clients ran, and nothing overlapped | the second client was refused while the first held the store |
| `store-outage` | baseline | control: the service stayed up and the run finished | failed loudly and recorded it; nothing was kept |
| `store-outage` | durable-inside | control: the store stayed available | failed closed, and the committed state was still there afterwards |
| `store-outage` | durable-outside | control: the run completed normally | the commit was refused and the run did not advance |
| `agent-call-failure` | baseline | control: the run completed with one call per step | the failed call was retried; the earlier step was skipped by its result file |
| `agent-call-failure` | durable-inside | not exercised: this variant's application code makes no agent call | not exercised: this variant's application code makes no agent call |
| `agent-call-failure` | durable-outside | control: the run completed with one call per step | the failure ended the run, so the resume had nothing to re-enter |

Controls present: **18**. A row without one says so rather than being quietly absent.

## Not exercised, and why

- `durable-inside` / `client-dies-mid-run`: D2's turn is a platform request served inside the hosted container; the harness has no client of its own to kill, and the platform's foreground-cancellation behaviour is documented rather than exercised here. Failing to kill something would have been reported as a missing result; this says which variant and why.
- `durable-outside` / `container-restart`: D3's state is a local store and its process is the run. Stopping it is a client death (client-dies-mid-run); there is no container of its own to restart, and pretending otherwise would have measured the harness's own process.
- `durable-outside` / `duplicate-delivery`: gate.sh refuses to overwrite an existing decision, so the duplicate is refused before any state is touched. That is worth knowing and is not a fault injection: the observation would be about the harness's own file-write guard.
- `durable-inside` / `agent-call-failure`: D2's application code (agent/durable_state.py, agent/d2_tool.py) writes the working state and parks an approval task. The agent call that produces the assessment is the platform's, made by the hosted turn before the tool runs. The D3 memo this fault is about has no counterpart here, so there is nothing to fail and nothing to memoise.

