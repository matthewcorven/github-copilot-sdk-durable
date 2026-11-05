# The restart — one session, one store, stopped and resumed

The claim this band exists to make: **an approval that parks on an external event survives
a container restart and resumes at the approval, not before it.** This file is the evidence
for it, captured from one run against the deployed agent. Every block below is pasted
unedited from the run directory, except where a line is marked as elided.

- Run: `run-20261105T203358Z/` — four turns, 7 s + 6 s + 7 s + 8 s of billed session
  compute (from each Response's own `created_at`/`completed_at`).
- `agent_session_id`: `b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe` — the **same
  id on all four turns**, asserted by `infra/invoke.sh` rather than observed by eye (§0).
- Agent version: **1** on all four turns, also asserted. A version change mid-run would
  mean the restart crossed images, and "the same agent resumed" would be a weaker claim
  than it looks.
- Store: `/home/session/durable/working-state.jsonl`, the same path and the same task record
  before and after (§3, §6).

---

## 0. The guards that make the capture mean what it says

Two checks in `infra/invoke.sh`, because both failure modes happened while this issue was
being built and neither is visible in a transcript:

```
   session confirmed: b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe
   agent version: 1
```

`assert_same_session` fails the run when a turn's `agent_session_id` is not the session the
run is about. The first complete attempt at this evidence **failed that check's absence**:
stage 2 was posted without the session id, so the platform created a second session, wrote
its assessment into a different store, and the "resume" captured a turn that had never seen
an approval. That run is deleted, not published — see `README.md` §"What was thrown away".

`assert_same_version` fails it when `agent_reference.version` changes between turns. The
second attempt straddled versions 3 and 4 because a `deploy.sh` ran while the run was live.
Corrected by waiting for one deploy to settle, then re-running.

Both checks are in the script, not in a reader's judgement: an evidence file that asks to
be trusted on a session id is not evidence of anything.

## 1. `ask` — a fresh container, and an empty store

The first turn creates the session. Nothing has been written yet, and the agent says so:

```
-- POST ask
   9s, exit 0
   status: completed | id: caresp_…  | session: b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe
```

The tool output, verbatim (`run-20261105T203358Z/10-ask.tools.txt`, elided only in repeated
JSON keys):

```
CALL working_state {"request_id":"PA-D2-0001"}
OUTPUT call_…
{
 "approval": null,
 "approvalTask": null,
 …
 "storePath": "/home/session/durable/working-state.jsonl"
}
CALL store_report {}
OUTPUT call_…
{
 "bytes": 0,
 "container": { … },
 "documents": [],
 "raw": "",
 "sha256": "no-file",
 "storePath": "/home/session/durable/working-state.jsonl",
 "tasks": []
}
```

`sha256: "no-file"` is the point: at this instant the store file **does not exist**. The
container is up, the tools are callable, and the working state is empty. Every later state
in this file is something a turn wrote.

## 2. `assess` — the assessment commits, and the approval parks

Same session (asserted). The model calls `record_assessment`; the tool commits the
assessment, creates the approval task and parks it on an external event:

```
CALL record_assessment {"request_id":"PA-D2-0001","assessment":"Coverage criteria are met."}
OUTPUT call_…
{
 "approvalTaskId": "t4",
 "awaitingEvent": "external:approval/PA-D2-0001",
 "created": true,
 "deadline": 1793997250.6636398,
 "note": "the approval is a durable task parked on an external event; nothing is held open",
 "phase": "awaiting-approval",
 "requestId": "PA-D2-0001"
}
```

Then the same turn reads the state back, so the park is observed and not only asserted:

```
CALL working_state {"request_id":"PA-D2-0001"}
OUTPUT call_…
{
 "approval": {
  "deadline": 1793997250.6636398,
  "decision": "",
  "reviewer": "",
  "state": "pending"
 },
 "approvalTask": {
  "checkpoint": {
   "deadline": 1793997250.6636398,
   "phase": "await",
   "requestId": "PA-D2-0001",
   "requestedAt": 1793910850.6636398
  },
  "id": "t4",
  "restarts": 0,
  "status": "WAITING",
  "waitingOn": [
   "external:approval/PA-D2-0001"
  ]
 },
 "assessment": "Coverage criteria are met.",
 …
 "phase": "awaiting-approval",
 "resumedCount": 0,
 "storePath": "/home/session/durable/working-state.jsonl"
}
```

Four things to read off it:

- `status: "WAITING"` and `waitingOn: ["external:approval/PA-D2-0001"]` — the approval is a
  task record parked on a name nothing in the store produces. There is no coroutine, no
  timer and no open request behind it; the response completed in 7 s.
- `deadline: 1793997250.6636398` — written into the checkpoint, not recomputed on read.
- `requestedAt: 1793910850.6636398` — the instant of the park.
- `deadline − requestedAt` = **86400.0 s exactly**, the 24 hours
  `durable_state.APPROVAL_DEADLINE_SECONDS` declares, computed once from one instant. The
  criterion that checkpoints stay identical will quote these two numbers.

## 3. `stop` — the container goes away

```
-- stop session b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe
   stop exit: 0
   (empty body: the action was accepted; the session object below is the proof)
   session-after-stop: status=idle created=1793910839 last_accessed=1793910857
```

`POST …/sessions/{id}:stop` answers **202 with an empty body**, so that is not evidence on
its own. The script captures the session object immediately afterwards
(`31-session-after-stop.json`):

```json
{
  "agent_session_id": "b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe",
  "created_at": 1793910839,
  "expires_at": 1796502857,
  "last_accessed_at": 1793910857,
  "status": "idle",
  "stopped_at": 1793910857,
  "version_indicator": { "agent_version": "1", "type": "version_ref" }
}
```

`status: idle` with a `stopped_at` is the platform saying the container is not running.
`created_at` is unchanged from §1's session — stopping does not create a new session.

Two invariants hold in this capture and in §4's, which is what makes them live reads rather
than a copy pasted twice:

- `expires_at − last_accessed_at` is **2 592 000** — exactly 30 days, to the second: the window
  D1 measured, renewed by the access this call itself made. In the same run, `ask`'s response
  is 6 s and `assess`'s 5 s, and the stop is served at 1793910857, 4 s after the last turn;
- `created_at` is **1793910839** in both, so the window slid forward and the session did not
  restart.

**What is not captured, and why.** No session object is taken right after `ask` or right
after `assess`, and none after `decide`. An earlier version of this script took all three, and
two of them — `11-session-after-ask.json` and `21-session-after-assess.json` — came back from
that earlier run **byte-identical to each other**, including `last_accessed_at`, although the
assess turn had run 12 s later. (That run's ids were `1793898161` for the access and
`1793898173` for the assess turn; both files hashed `e50b23b0…`.)

That cannot be a live `GET` after the assess turn: `last_accessed_at` is the platform's last
*access*, and it does not appear to move for a turn until the next one is served. A capture
that cannot be distinguished from a copy is not evidence, so those files are deleted and the
script no longer emits them. The two that remain bracket the transition the
platform performs itself, and their `stopped_at` / `status` / `last_accessed_at` are the
platform's own change.

## 4. `resume` — a new process reads the park, and re-enters at it

The prompt names the request id and nothing else. It does **not** name the approval task id
or the deadline; the only place those exist is the store the stopped container wrote.

The request, verbatim (`40-resume.request.json`):

```json
{
 "input": "Request PA-D2-0001 again. This container was stopped and restarted. Read your durable working state with working_state, then call resume_work, and tell me: which approval task you found, what status it was in, the exact deadline value stored with it, and whether anything about this request had to be redone. Quote the values, do not summarise them.",
 "stream": false,
 "agent_session_id": "b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe"
}
```

The tool output, verbatim (`40-resume.tools.txt`):

```
CALL working_state {"request_id":"PA-D2-0001"}
OUTPUT call_…
{
 "approval": {
  "deadline": 1793997250.6636398,
  "decision": "",
  "reviewer": "",
  "state": "pending"
 },
 "approvalTask": {
  "checkpoint": {
   "deadline": 1793997250.6636398,
   "phase": "await",
   "requestId": "PA-D2-0001",
   "requestedAt": 1793910850.6636398
  },
  "id": "t4",
  "restarts": 0,
  "status": "WAITING",
  "waitingOn": [
   "external:approval/PA-D2-0001"
  ]
 },
 "assessment": "Coverage criteria are met.",
 …
 "resumedCount": 0,
 "storePath": "/home/session/durable/working-state.jsonl"
}
CALL resume_work {"request_id":"PA-D2-0001"}
OUTPUT call_…
{
 "approval": { "decision": "", "reviewer": "", "state": "pending" },
 "decision": "",
 "phase": "awaiting-approval",
 "requestId": "PA-D2-0001",
 "resumedCount": 1,
 "settled": {
  "error": null,
  "result": null,
  "status": "pending",
  "taskId": "t4"
 }
}
```

This is the claim, and it is four separate facts:

1. **`deadline: 1793997250.6636398` is byte-identical to §2.** A container that was not running
   when it was written read it back unchanged. A deadline recomputed at resume would be
   `now + 86400` and would differ; this one did not move.
2. **`requestedAt: 1793910850.6636398` is byte-identical too** — the park time, not the resume
   time. If the run had re-entered the `park` phase, this value would have been rewritten.
3. **`assessment: "Coverage criteria are met."` came back**, so the assessment step was not
   redone: the resumed turn read the value the stopped container committed.
4. **`resumedCount` went `0 → 1`.** That increment is the only write this turn made to the
   work state, and it is how a restart is visible in the document rather than asserted in
   prose.

`resume_work` re-entered the task and reported `status: "pending"` — the run ended at the
park again, because no answer exists yet: `taskId: t4`, `result: null`, `error: null`.
**A task that resumes into a park is not a failed task.** Before this issue's layer change
the same run settled as `failed / "task did not finish"`, which reads as "this work cannot
continue" for a task the next event can still wake (`durable/GAP.md` §"The reproduction").

`created_at`/`completed_at` on the four Responses are `1793910843→1793910848` (ask),
`1793910849→1793910854` (assess), `1793910868→1793910874` (resume) and
`1793910875→1793910887` (decide): the gap between 1793910854 and 1793910868 is the stop and
the five-second settle, and nothing was running for the rest of the 24-hour window the
approval was open.

`restarts: 0` here is correct and easy to misread. The counter increments when a run re-enters
a task that was `running` — one interrupted mid-phase. This task was `waiting`, a clean park,
so re-entering it is the designed path and the counter stays put. It does move to `1` on the
turn that completes the task (§5), because that run re-enters a `running` task the delivery
woke.

Session continuity, for the record (`41-session-after-resume.json`):

```json
{
  "agent_session_id": "b34699f92021c4e3a04d0eda60edfac6e4dcfe8fcdb28cac480204ae83aeabe",
  "created_at": 1793910839,
  "expires_at": 1796502864,
  "last_accessed_at": 1793910864,
  "status": "active",
  "version_indicator": { "agent_version": "1", "type": "version_ref" }
}
```

Same `created_at`, `status: active` again, `version_indicator` still 1.

## 5. `decide` — the event wakes the task, and it completes at the approval

```
CALL record_approval {"request_id":"PA-D2-0001","decision":"approved","reviewer":"Dr. Chen","reason":"coverage criteria met; PET-avid nodule meets imaging criteria"}
OUTPUT call_…
{
 "finalPhase": "decided",
 "note": "the task resumed at the step after the approval",
 "requestId": "PA-D2-0001",
 "settled": {
  "error": null,
  "result": {
   "decision": "approved",
   "reason": "coverage criteria met; PET-avid nodule meets imaging criteria",
   "reviewer": "Dr. Chen"
  },
  "status": "completed",
  "taskId": "t4"
 },
 "wokenTasks": [ "t4" ]
}
```

Followed by `working_state` on the same turn:

```
{
 "approval": {
  "deadline": 1793997250.6636398,
  "decision": "approved",
  "reviewer": "Dr. Chen",
  "state": "answered"
 },
 "approvalTask": {
  "checkpoint": {
   "deadline": 1793997250.6636398,
   "phase": "await",
   "requestId": "PA-D2-0001",
   "requestedAt": 1793910850.6636398
  },
  "id": "t4",
  "restarts": 1,
  "status": "TERMINAL",
  "waitingOn": []
 },
 "assessment": "Coverage criteria are met.",
 "decidedAt": "2026-11-05T20:34:36Z",
 "decidedBy": "Dr. Chen",
 "decision": "approved",
 "phase": "decided",
 "requestId": "PA-D2-0001",
 "resumedCount": 1,
 "storePath": "/home/session/durable/working-state.jsonl"
}
```

The checkpoint still says `phase: "await"` and carries the same `deadline` and `requestedAt`.
That is the mechanism working: the completed run re-entered at `await`, the phase the park
wrote, and never entered `park` again. A task that re-ran from its first phase would have a
fresh `requestedAt`, a fresh deadline and a second park in the approval document — none of
which is present.

`restarts: 1` — this run entered a task the delivery had left `running`, the counter's
definition.

## 6. The store, verbatim — and the same task before and after

The final `store_report` (`50-decide.tools.txt`):

```
"bytes": 5898,
"sha256": "f257d23ab62616a535fa9ee330f20bf8069e21e83f9c46d60177b5e71b46b442",
"files": {
 "directory": "/home/session/durable",
 "files": [
  { "bytes": 5898, "mtime": "2026-11-05T20:34:36Z", "name": "working-state.jsonl" },
  { "bytes": 38, "mtime": "2026-11-05T20:34:25Z", "name": "working-state.jsonl.lock" }
 ],
 "pid": 5
}
```

`pid` is **not** evidence of a restart, and this file does not use it as any. It is 5 in
the first turn, in the last, and in the control's separate session: it is the image's entry
point, not a per-container process id. One earlier capture showed 5 then 6, and that
difference was read as the restart — it was coincidence, and `evidence/container-filesystem.md`
§4 records the correction. What carries the restart is the `agent_session_id`, the unchanged
`created_at`, and the byte-identical park timestamps — all asserted or quoted above.

The `working-state.jsonl` text, 25 JSONL records: every task record, quoted as the checkpoints
this file relies on:

```
{"v":1,"seq":4,"type":"task","payload":{"id":"t4","name":"d2.approval-task","input":{"requestId":"PA-D2-0001"},"checkpoint":{"phase":"park"},"status":0,"restarts":0,"waitingOn":[],"memos":{}}}
… task status 2  checkpoint {"phase":"await","requestId":"PA-D2-0001","deadline":1793997250.6636398,"requestedAt":1793910850.6636398}  waitingOn ["external:approval/PA-D2-0001"]  memos {}
… task status 1  checkpoint (same)  waitingOn []  memos {"external-event":{"decision":"approved","reason":"…","reviewer":"Dr. Chen"}}
… task status 1  checkpoint (same)  restarts 1  memos {"external-event":…}
… task status 4  checkpoint (same)  waitingOn []  restarts 1  memos {"external-event":…}
```

Read as a sequence, that is the whole band in five records:

| record | `status` | `checkpoint.phase` | `waitingOn` | `memos` |
| --- | --- | --- | --- | --- |
| created | `0` (`Pending`) | `park` | `[]` | `{}` |
| parked | `2` (`Waiting`) | `await` | `["external:approval/PA-D2-0001"]` | `{}` |
| woken | `1` (`Running`) | `await` | `[]` | `["external-event"]` |
| re-entered | `1` (`Running`) | `await` | `[]` | `["external-event"]` |
| finished | `4` (`Terminal`) | `await` | `[]` | `["external-event"]` |

The **same `deadline` and `requestedAt` appear in every one of those records**. The approve
answer lives in `memos["external-event"]` — the layer's delivery channel — and `waitingOn` is
empty from the moment it arrived. The task never moved back to `phase: "park"`.

The two documents:

| document | versions | entries | what they are |
| --- | --- | --- | --- |
| `d2.work-state@PA-D2-0001` | 4 | `e1`, `e8`, `e11`, `e17` | assessment, approval created, resume, decision |
| `d2.approval@PA-D2-0001` | 2 | `e5`, `e14` | parked → pending, answered |

```json
e5  {"requestId":"PA-D2-0001","state":"pending","deadline":1793997250.6636398,"requestedAt":1793910850.6636398,"decision":"","reviewer":"","reason":""}
e14 {"requestId":"PA-D2-0001","state":"answered","deadline":1793997250.6636398,"requestedAt":1793910850.6636398,"decision":"approved","reviewer":"Dr. Chen","reason":"…"}
```

`d2.approval` is `Rewindable`, so the park the stopped container wrote and the answer the
resumed container wrote are both in it. The store can say who decided, when, and against
which deadline — what an auditor reads, and what a state file the agent overwrites cannot.
(It has two versions, not three: the park is one commit, because the deadline was computed
from one instant. An earlier revision of this scenario took `time.time()` twice and produced
a third record with a `requestedAt` four milliseconds off the checkpoint's — the kind of
drift that makes a restart look like a second park.)

## 7. The control — a fresh session has no store

`./infra/invoke.sh --control` posts stage 1's question with **no** `agent_session_id`, so the
platform starts a new container (`evidence/control-session-20261105T203511Z/`):

```
   status: completed | session: cd33a4fc3a7472467d4b3343179bcc0eb7cc9862fe76a728bcc77e29df14810
```

The control run makes two ask-turns: the first creates the session it then leaves, and the
second — the actual control — is posted without a session id and lands on
`cd33a4fc…`, which is neither the run's session (`b34699f9…`) nor the one the first turn
created.

```
OUTPUT …
{
 "bytes": 0,
 "container": { … },
 "documents": [],
 "raw": "",
 "sha256": "no-file",
 "storePath": "/home/session/durable/working-state.jsonl",
 "tasks": []
}
```

The control names **the same store path** — `/home/session/durable/working-state.jsonl` — and
reports it empty, with `sha256: "no-file"`, `"documents": []`, `"tasks": []`. Its directory
listing holds only the lock sidecar (`38` bytes), which is the store's *open*: nothing had
been written. That pair is the argument:

- the path is not per-container: the same path on a fresh sandbox is a different, empty file;
- so the state §4 read was not "whatever this container happens to have". It was written by
  session `b34699f9…` before the stop, and it survived because it lives on the platform's
  session volume and nowhere else.

The control's own assertions came from the tool, not the prose: `bytes: 0`, `documents: []`,
`tasks: []`, `sha256: "no-file"`, and a `files` listing with the lock and no store.

## 8. The same restart without a platform

`durable/local-restart.sh` runs the identical sequence as four processes over one file —
assess, park, resume with no answer, deliver, resume, read the marker — and is what makes
this claim reproducible for someone with no subscription. Captured in
`evidence/local-restart/`:

```
LOCAL assess: {"approvalTaskId": "t4", "deadline": 1793997420.6816819, "deadlineIso": "2026-11-06T20:37:00Z", "event": "external:approval/LOCAL-1", …}
LOCAL task:   {"checkpoint": {"deadline": 1793997420.6816819, "phase": "await", "requestId": "LOCAL-1", "requestedAt": 1793911020.6816819}, "status": "WAITING", "waitingOn": ["external:approval/LOCAL-1"]}
04 (resume, no answer): "settled": {"status": "pending", "taskId": "t4"}, "resumedCount": 1
05 (deliver):           {"woken": ["t4"]}
06 (resume, answered):  "phase": "decided", "decision": "approved", "decidedBy": "Dr Okoro", "resumedCount": 2,
                        task {"status": "TERMINAL", "restarts": 1, "waitingOn": [],
                              "memos": {"external-event": {"decision": "approved", "reason": "criteria met", "reviewer": "Dr Okoro"}}}
07 (marker):            "writtenAt": "2026-11-05T20:37:01Z", "writtenByPid": 38354
```

`deadline − requestedAt` is again exactly 86400.0 s, and both are the same in the document
and in the task's checkpoint — the criterion that the park and its deadline are one event,
checked without the platform in the way.

`07-marker-read.json` reads a file **process A wrote** (`writtenByPid` is A's pid), so the
directory survived every process boundary. The store's digest differs across the run
(`a1c8455…` after process A, `e0ee9db…` after process D) because processes wrote to it — which
is the difference between a surviving file and a frozen one.

## 9. What this does not prove

- **Not a crash.** The stop is graceful: the process is given the chance to finish its
  commit before the container goes away. A `kill -9` mid-commit is a different question —
  the store's torn-tail recovery and the layer's atomicity — and it is D4's, not this
  band's. What is proven here is that the *park* is a durable record, not a live process.
- **Not the 900 s idle window.** The stop is explicit. The idle timeout produces the same
  transition (D1's `../band-d-hosted-foundry-agent/evidence/platform-behaviour.md` §2 shows the state machine), but waiting
  fifteen minutes would not strengthen this file, so it was not waited out.
- **Not concurrency.** One request, one approval, one decision. Two processes over one store
  is covered by the layer's own lock tests, not here.
- **Not the whole accelerator.** Same scope as D1: one decision path through the platform,
  with clinical and compliance absent by design.

## 10. What the model said, and why the tool output is the evidence

The assistant message on some turns disagrees with the tool output in the same response —
in the first, discarded attempt it reported `"approval task: null"` while its own
`working_state` call had just returned `t4`. A model summarising is a witness, not the
record. So every claim above is quoted from `*.tools.txt`, which is the function-call output
captured from the response itself, in call order. `*.result.txt` keeps the assistant's prose
beside it, and where the two differ the tool output is what this file relies on.