# `agent/` — the two things the platform runs, and the state behind them

| File | What it is |
| --- | --- |
| `main.py` | The hosted agent. Five tools over the durable working state; served by `ResponsesHostServer` on 8088. The transcript is not mirrored. |
| `durable_state.py` | The layer as this agent uses it: `WorkState` and `Approval` documents, the approval task, store resolution, container facts. |
| `d2_tool.py` | The same operations from a shell — `assess`, `resume`, `approval`, `state`, `probe` — and what `../durable/local-restart.sh` drives. |
| `Dockerfile` | The image. Same base, user, port and health check as D1; `lib/py` installed from the build context `deploy.sh` assembles. |
| `requirements.txt` | The hosting stack (with D1's two dependency-range corrections) plus the SDK the layer imports. |

## The five tools

| Tool | Writes | Used by |
| --- | --- | --- |
| `working_state(request_id)` | nothing | every turn, to read the position |
| `record_assessment(request_id, assessment)` | work state + approval document + the task, parked | `assess` |
| `resume_work(request_id)` | `resumedCount`, and the task's re-entry | `resume` |
| `record_approval(request_id, decision, reviewer, reason)` | delivers the event, then resumes the task to terminal | `decide` |
| `store_report()` | nothing — reads the file, its digest and its text | evidence |

`record_assessment` is idempotent by the store: a request that already has an approval keeps it,
and keeps its deadline. Re-opening a gate would discard the event its task is parked on.

## Why the approval is a tool and not a round-trip

A hosted agent's turn is a request. A person answering is not. Making the turn wait for a person
would hold a billed container for the length of a deliberation, and would not survive a restart.
So the tool is how the answer is *written down*, and the waiting is a `waiting` checkpoint with a
deadline — `TaskCommit.WaitingForEvent`. `durable/GAP.md` is why the layer needed a new commit for it.

## Why `_store()` is a lazily-opened singleton

A JSONL store holds the file's exclusive lock for its lifetime. Two handles in one container would
mean the second open fails, which is the store's own guarantee — so the agent opens once and keeps
it. `store_report`'s `files` block shows the lock sidecar this creates, and that it is the only
sidecar on the volume.

## Order of operations that matters

`record_approval` delivers the event **and then** resumes the task. That order is the crash-safety
design: the delivery is the durable fact (one transaction, memo written with the wait cleared), and
the resume is what produces the outcome. A crash between them leaves a `running` task with its
answer memo — the next resume completes it. The reverse order would resume a task that has no
answer and re-park it, which is correct but costs a round trip; worse, a crash after the resume and
before the delivery would leave a completed task and an undelivered event.
