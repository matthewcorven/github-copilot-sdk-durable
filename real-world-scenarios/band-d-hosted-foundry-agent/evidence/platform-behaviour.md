# Platform behaviour — four questions, each marked documented or observed

Cited against the documentation read on **2026-11-05** and against the deployment
this issue created (East US 2, hosted agent `coverage-assessment-agent` version 1,
`gpt-5.4` GlobalStandard, session configuration left at the platform default).

| # | Question | Answer | Documented | Observed here |
| --- | --- | --- | --- | --- |
| 1 | Session persistence window | **30 days**, measured from session creation, renewed by activity | yes | yes |
| 2 | Idle timeout | **900 s (15 min) default**, configurable 120 s – 14 400 s per agent version | yes | only the effect (see caveat) |
| 3 | Foreground response when the client disconnects mid-run | The response is **cancelled**; the in-flight work is **not** preserved for a later lifetime | yes | yes |
| 4 | Does the platform replay it? | **No** for foreground. Replay is a property of *stored background* responses with `resilient_background` opted in; even then recovery **re-enters the handler from its beginning**, it does not replay deterministically | yes | not exercised (see §4) |

The summary the matrix needs is one sentence: **the platform keeps the session and the
transcript; it does not keep in-flight work.** Recovery of in-flight work is the
builder's job, and the builder's tool for that is documented but not on by default.

---

## 1. Session persistence window — documented and observed

**Documented.** [Manage hosted agent sessions](https://learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions):

> The platform persists the session's filesystem ($HOME and uploaded files) across
> turns and across idle periods, so the agent can resume where it left off. **Sessions
> persist for up to 30 days.**

**Observed.** `GET {project}/agents/{name}/endpoint/sessions/{id}?api-version=v1`
returns the window directly. From `evidence/session-resume/session-before.json`, the
session created implicitly by the first run:

```
created_at       1793882817   = 2026-11-05T12:46:57Z
expires_at       1796474817   = 2026-12-05T12:46:57Z
last_accessed_at 1793882817
status           active
```

```
$ python3 -c "print(round((1796474817-1793882817)/86400, 6))"
30.0
```

Two facts worth separating, both visible in the three captures in
`evidence/session-resume/`:

- `expires_at − created_at` is **exactly 30 days** on the untouched session.
- `last_accessed_at` moves on each request and `expires_at` moves with it: after a
  `:stop` at 1793882878 (61 s later) `expires_at` became 1796474878, and after the
  next turn at 1793882890 it became 1796474890. The window is *sliding on activity*,
  not a hard 30-day cliff from creation. The document says "persist for up to 30
  days"; the API shows the sliding form.

The persisted thing is **the sandbox, not the conversation**. The same page:

> Session continuity isn't the same as conversation continuity. For Responses,
> preserve message history across turns by passing `previous_response_id` or a
> conversation ID. […] In both cases, `agent_session_id` only ties calls to the same
> sandbox.

## 2. Idle timeout — documented; only its effect is observable

**Documented.** Same page:

> The agent version's idle timeout can be 2 through 240 minutes (4 hours) and defaults
> to 15 minutes. When the timeout is reached, the platform **deprovisions compute and
> saves state until the session is referenced again**.

and, in the REST form:

> Set `idle_timeout_seconds` from 120 through 14 400 seconds (2 minutes through 4
> hours). **If you omit the setting, the server default is 900 seconds (15 minutes).**

**Observed, partially.** The version was created **without**
`session_configuration` (deliberately — see `infra/deploy.sh`, which says why), so the
900 s default applies. The version object confirms no override:

```
$ az rest --method GET .../agents/coverage-assessment-agent/versions/1
{ "status": "active", "definition": { "cpu": "1", "memory": "2Gi", ... } }   # no session_configuration
```

**Caveat, stated because it matters:** the 15-minute eviction itself was **not**
waited out. A 15-minute idle window would have doubled this issue's wall time for a
fact the API already exposes as a default and the docs state twice. What *was* observed
is the state machine the timeout belongs to — `active` → `idle` on an explicit
`:stop` (§3) → `active` again on the next turn, with the same `agent_session_id` and a
preserved `$HOME`. That is the same transition the timeout triggers; only the trigger
differs (explicit stop vs. no traffic for 900 s).

## 3. A foreground response when the client disconnects mid-run — observed

**Documented.** [Long-running agent resilience](https://learn.microsoft.com/azure/foundry/agents/concepts/long-running-agent-resilience):

> For the Responses protocol, **full crash recovery applies only to stored background
> responses when the server opts in to resilient background execution. Foreground
> responses remain tied to the client connection and aren't reinvoked after a process
> interruption.**

**Observed.** A foreground run takes ~40 s against this agent. The client abandoned the
connection after **5 s**:

```
$ python3 ... urllib.request.urlopen(req, timeout=5)
TimeoutError: The read operation timed out          # client gave up at t+5s
```

The platform did not lose the call — it resolved it, as `cancelled`:

```json
$ az rest --method GET .../endpoint/protocols/openai/responses/caresp_0aee7f34a76f560900GchHP7ShQJ7z17Lm9A1CKyh9RHxeXwvb
{
  "id": "caresp_0aee7f34a76f560900GchHP7ShQJ7z17Lm9A1CKyh9RHxeXwvb",
  "object": "response",
  "status": "cancelled",
  "background": false,
  "agent_session_id": "a135b10e6abc33d2ac97084d4c36643b698494282b0aa3976fb4b90d30b21e4",
  "created_at": 1793882866
}
```

Three things this establishes, and one it does not:

1. The response record **exists** after the client is gone. The platform records the
   attempt.
2. Its terminal state is **`cancelled`, not `failed` and not `completed`**. The work
   was abandoned, not retried, not finished in the background — `background: false`.
3. A **session was created anyway** (`a135b10e…`, `status: active`, also 30 days) and
   survives the cancelled response. The sandbox and the response have independent
   lifetimes.
4. It does **not** establish anything about a busy-*handler* case: the cancellation here
   could have landed before or between tool calls. That distinction — "cancelled at a
   checkpoint" vs "cancelled mid-tool" — is D4's territory, not D1's.

The same outcome is what the docs describe for a handler that is lost: a *foreground*
response "is always marked failed on crash, because their client connection is already
gone" ([recover long-running work](https://learn.microsoft.com/azure/foundry/agents/how-to/recover-long-running-work)).
Here the connection going away is *sufficient* by itself — no crash needed.

## 4. Does the platform replay it? — documented; not exercised

**Documented, and the answer is no for a foreground response.** The only replay the
platform offers is:

1. the response must be **stored and background** — `store=true` *and* `background=true`;
2. the server must **opt in** with `ResponsesServerOptions(resilient_background=True)`;
3. and even then, what happens is not replay of the work:

> **Recovery reenters the handler from its beginning. It isn't deterministic replay,
> and it doesn't restore local variables or an in-memory call stack. The handler uses
> durable checkpoints or watermarks to determine which work is already complete.**

What *is* replayed is the **stream**, not the work:

> Replayable streams retain events and assign cursors that clients use when they
> reconnect. […] For a recovered Responses stream, a later `response.in_progress` event
> is a snapshot reset. A client replaces its locally accumulated output with the
> snapshot in that event, discards partial output that isn't in the snapshot, and then
> applies subsequent events. Output indexes identify slots in the current snapshot;
> they aren't guaranteed to increase across recovery attempts.

**Not exercised, and why.** The vendored coverage agent (and the accelerator) use
`ResponsesHostServer(agent).run()` with default options — no `resilient_background`,
no `store=true`/`background=true` request. Turning that on for this issue would have
produced a *different* baseline from the one the accelerator ships, which is what
D2/D4 compare against. So this row is **documented only**, and the matrix should
quote the document rather than this deployment for it.

## 5. Session continuity vs. conversation continuity — observed, and it constrains D2

The platform keeps a **sandbox**; it does not, by itself, feed prior turns back to the
model. Two observations from the same deployment make the boundary concrete:

**(a) A stopped session resumes with its context intact.** Session
`6f7126f7…` (created by the first run) was stopped explicitly:

```
$ az rest --method POST .../sessions/6f7126f7…:stop
$ az rest --method GET  .../sessions/6f7126f7…
{ "status": "idle", "stopped_at": 1793882878, "last_accessed_at": 1793882878,
  "expires_at": 1796474878, "version_indicator": {"agent_version": "1"} }
$ az rest --method GET  .../sessions/6f7126f7…        # after the follow-up turn
{ "status": "active", "last_accessed_at": 1793882890, "expires_at": 1796474890, ... }
```

Then a follow-up turn was sent with `agent_session_id: 6f7126f7…` and no other
context. It completed, in the **same** session, and the agent answered from its
earlier assessment. The timing is the artifact's, not the stopwatch's —
`resume-response.json` records `created_at 1793882892` and `completed_at 1793882898`
(2026-11-05T12:48:12Z → 12:48:18Z, **6 s** inside the platform); the client's own
wall clock for the same call, including connection setup, was 10.5 s:

```json
{"criterion": "Provider Specialty-Procedure Appropriateness", "status": "MET",
 "confidence": 95, "source": "Prior assessment"}
```

Session state came back; the session flipped to `active`.

**(b) A fresh session has no prior context at all.** The same follow-up with no
`agent_session_id` created a new session and the agent answered:

```json
{"error": "NO PRIOR CONTEXT",
 "checks_performed": [{"rule": "Prior assessment availability in current conversation",
                       "result": "not_found"}]}
```

**The control ran on agent version 2.** `fresh-session-control.json` carries
`"agent_reference": {"name": "coverage-assessment-agent", "version": "2"}` while the
session objects and `resume-response.json` are version 1: a second `deploy.sh` run
between the two — the documented `create_version()` behaviour — re-registered the
agent with the same image and configuration. `evidence/session-resume/README.md`
lays out the order. It changes nothing here: the control's claim rests on its new
session id and its `NO PRIOR CONTEXT` answer, not on the version, and both turns ran
the same image.

`evidence/session-resume/` holds both responses. Read together, (a) and (b) say the
resumption above was **session-resident state**, not the platform replaying the
transcript: the model was never given a message list it did not keep.

Why this matters for the band: the accelerator's `store: False`
(`upstream/agents/coverage/main.py:177`) is not a bug — the platform manages
conversation history at the *conversation* layer, so a second copy inside the model
call would be duplication. The honest baseline for D2 is therefore: **the platform
owns the transcript and the sandbox; Durable has to own the working state and the
memos** — and if D2 also wants the transcript, it should acquire it deliberately, not
infer it from `agent_session_id`.

## 6. Nothing was left behind

`infra/teardown.sh` is the last thing this issue ran. It deletes the resource group
that contains every provisioned resource and verifies with `az resource list` that
nothing remains; a second invocation is harmless. The captured output is in
`evidence/teardown.md`.
