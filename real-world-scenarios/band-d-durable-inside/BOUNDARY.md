# Band D (D2) — the boundary between what Foundry owns and what Durable owns

Two stores are in play in this scenario, and the point of the band is that they are
different stores with different owners. This file states the split, and — the part that
matters when something goes wrong — **which one wins**.

The short version: **Foundry owns what was said; Durable owns what is true.** The transcript
records the conversation. The working state records the decision, its inputs and its
deadline. When they disagree, the working state wins for every question of fact, and the
transcript is the record of the disagreement.

---

## 1. The split

| Fact | Lives in | Why there | Survives a restart | Survives a new session |
| --- | --- | --- | --- | --- |
| The user's messages and the model's replies | **Foundry** — the Response objects and the conversation the platform keeps | The platform manages conversation history in the refreshed preview (`store: False`), and the agent is not a place to keep a second copy | yes, same session | **no** (a new session is a new conversation) |
| Tool calls and their results, as the model saw them | **Foundry** — in the Response's `output` | They are part of the conversation's turn; re-deriving them would be a replay the platform does not offer | yes, same session | no |
| Which step a request is at (`intake` → `awaiting-approval` → `decided`) | **Durable** — `d2.work-state@<requestId>` | It is the agent's position in *its own* workflow, not something anyone said | **yes** | **yes** |
| The coverage assessment the agent produced | **Durable** — the same document | It is an input to a decision and must be readable when no conversation exists | yes | yes |
| The approval: who was asked, the deadline, the answer, the reviewer, the reason | **Durable** — `d2.approval@<requestId>`, `Rewindable` | An auditor asks "who approved this, when, and by what deadline"; a transcript cannot answer the deadline half | yes | yes |
| That the approval is *waiting*, and on what | **Durable** — the task record `t4`: `status`, `waitingOn`, `checkpoint.phase` | It is the difference between "nobody has answered yet" and "the answer was lost" | **yes** | yes |
| The durable deadline across a stop | **Durable** — `checkpoint.deadline` on the task, mirrored to the document | A document alone would be a claim no task enforces; the checkpoint is what the resumed run reads | yes | yes |
| How many times a process has picked the request up | **Durable** — `resumedCount` on the work state, `restarts` on the task | It is how a restart is observable rather than assumed | yes | yes |
| The `agent_session_id`, the session's expiry, the container's identity | **Foundry** — the Response and the session object | The platform's, and the agent has no business re-deriving it | yes (same session) | new id by definition |
| The container's filesystem facts (writable paths, `$HOME`, the store path) | **Durable**'s `store_report` *reports* them; they are **Foundry's** environment | Measurements, captured per process — not state | n/a | n/a |

**What is deliberately not mirrored.** Nothing from the transcript is copied into the store.
The scenario's system prompt says so, and the store's documents contain no message list, no
tool-call log and no model output beyond the assessment the agent chose to commit as a step.
That is D1's finding (`../band-d-hosted-foundry-agent/evidence/platform-behaviour.md` §5)
applied rather than restated: the platform keeps the transcript, so a second copy inside the
model call would be duplication, and a second copy in the store would be a stale transcript.

## 2. The conflict rule

A conflict is not hypothetical. Three shapes of it were observed or created while building
this:

| Conflict | Example | Who wins | Why |
| --- | --- | --- | --- |
| The transcript says a step happened; the store has no record of it | A turn's assistant message claims "the approval is pending" while its own `working_state` call returned nothing | **Durable** | The store is the agent's state; the message is a summary a model produced and can be wrong. §10 of `evidence/restart-across-stop.md` is the measured case: the message and the tool output in the *same* response disagree. |
| The transcript says a decision was made; the approval document still says `pending` | A model says "I approved it" without calling `record_approval` | **Durable** | A decision nobody wrote down is not a decision. `record_approval` is the only path that writes it, and the tool refuses a decision value it does not recognise. |
| The store says a decision was made; the transcript's last message did not mention it | A turn crashed after `record_approval` committed, before the model's final message | **Durable** | The commit is the fact; the message is narration. The transcript being incomplete is a transcript problem, not a state problem. |
| Two sessions have a store for the same request id | Impossible here: the store lives on the session volume | — | A *new* session has an empty store (the control in §7), so "the same request in another session" means "another working state", which is correct. |
| The task is `Terminal`; a late event arrives | `RaiseEventAsync` after the decision | **Store** — the delivery is a no-op, `wokenTasks: []`, and the first answer is not overwritten | The first answer is the answer; a redelivery is a duplicate, and `CommitTaskAsync` also refuses to overwrite a terminal outcome. |
| The deadline has passed but the transcript says the approval is still live | Impossible to observe without waiting 24 h here; the phase is what enforces it | **Durable** | `await` checks `time.time() >= deadline` on the run an event starts, and fails the task. The document never decides the outcome alone. |

The rule in one sentence: **state of record wins for facts, transcript wins for what was
said.** If you need to know what a person was told, read Foundry. If you need to know whether
a request was approved, read Durable. Neither is allowed to answer the other's question, and
the agent is instructed to quote the tool, not to narrate.

### Why the rule is not "Foundry wins because it is the platform"

Two candidates were rejected.

- **Foundry wins.** The platform's transcript is the *earliest* record, not the most
  authoritative one: it is written by a model, it can be truncated (a session has a length
  limit), and in this preview it is deliberately not stored by the agent (`store: False`).
  Making it authoritative would mean parsing a conversation to decide whether a patient was
  approved, and a restarted process would have to re-read the transcript to know its own
  position — which is precisely the non-durable design this band exists to replace.
- **A single store for both.** Copying the transcript into Durable would make the *agent*
  the owner of conversation history, which disagrees with the platform for no gain. It would
  also have to be kept in sync with `previous_response_id`/conversation semantics the
  platform already implements, and the store would double in size per turn. D1 measured the
  duplication cost (coverage re-sends its whole conversation per model call; the input side
  is what scales).

The split is therefore not a compromise: each side owns the question it can answer exactly,
and the one place they can disagree — "the model said so" — is resolved by refusing to treat
narration as state.

## 3. What each side is *not* allowed to do

| Foundry must not | Durable must not |
| --- | --- |
| Be the place the agent looks up its own position | Hold the transcript |
| Be re-parsed to decide whether an approval happened | Be a cache of conversation turns |
| Be assumed to survive a *new* session | Be assumed to survive the session volume being gone |
| Be assumed to answer "when does this expire" | Decide an outcome without a task run |

The last row of each column is the one that bit during development:

- the platform's transcript is not the state — the first, discarded run "resumed" a session
  that had never written an assessment because stage 2 ran on a **different session**
  (a new container, a new empty store). The transcript looked continuous; the store proved
  it was not. That is why `invoke.sh` now asserts the session id on every turn.
- the store cannot decide alone — the approval document could say `pending` forever while its
  task has expired. The task's `await` phase is the enforcer, and the document is the record
  of what it enforced.

## 4. Where this shows up in the code

| Boundary | Code | Note |
| --- | --- | --- |
| Transcript is not read | `agent/main.py` — no tool reads a conversation | There is no `previous_response_id` plumbing, deliberately |
| Store is not the transcript | `agent/durable_state.py` — `WorkState` has 8 fields, none a message | `assessment` is a committed *step*, not a reply |
| A step commits before it is reported | `record_assessment` commits, then returns the state | The tool output is the store's value, not a promise |
| A decision commits with the task's end | `ApprovalTask.await` completes the task with the documents in the same transaction | A store cannot hold "decided" with a waiting task |
| A restart re-enters at the park | `resume_approval` → `wait_for_task_async` | It reads the checkpoint; it does not re-run `park` |
| An event is the answer | `Workspace.deliver` → `raise_event_async` | One transaction: memo written, `waitingOn` cleared |

## 5. The honest limit

This boundary works because the working state is *small*: one request's position, its
assessment, its approval. A workflow whose state *is* a long transcript — a coding agent
retrying a build, say — would need a different split, because "do not mirror the transcript"
and "resume mid-turn" cannot both hold when the state is the conversation. D3 (the Durable
variant *outside* the platform) and D4 (multi-agent) are where that shape gets tested; this
band's claim is narrower and it is proven: the platform can own the conversation while the
agent owns its own state, and a restart lands on the approval instead of before it.