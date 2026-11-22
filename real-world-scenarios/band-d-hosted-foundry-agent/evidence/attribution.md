# D4 — attribution: which observations are the platform's, and which are ours

Every fault result in `faults/results/` carries a `platform` block and an `application` block. This
file is the reasoning behind that split, one row per documented platform behaviour: what the source
says, what this run could actually observe, and what a future run with a deployment would have to
show to move the status.

The statuses are exactly two, and `faults/lib/result.py` refuses a third:

- **`documented, not exercised here`** — the answer is quoted from the documentation and D1's captures.
- **`reproduced here`** — this run caused the behaviour and observed it.

No result in this directory reaches the second status, and that is not modesty: `platform.live_foundry_called`
is `false` in every one of them because **D4 provisioned nothing**. The check in `lib/result.py` exists
so that a future run cannot quietly promote a row by writing prose that sounds like a measurement.

---

## The four behaviours

### 1. Session persistence — `documented, not exercised here`

**Quoted.** [Manage hosted agent sessions](https://learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions):
the platform persists the session's filesystem across turns and idle periods, **for up to 30 days**,
and `expires_at − created_at` on D1's live session was exactly 30 days, sliding with activity
(`../evidence/session-resume/`).

**What this band could observe.** A Docker container that is stopped and started over one volume keeps
its files. That is a fact about Docker. Reporting it as "the platform's session persisted" is precisely
the substitution the issue was filed to prevent, so the `container-restart` rows say it is a local
analogue in their own `platform.what_the_platform_is_not_represented_by`.

**What would move it to `reproduced here`.** A deployed hosted agent, a `:stop` on the session, a turn
in the same `agent_session_id`, and the session object's `created_at` unchanged with the state readable.
D1 did exactly that once (`../evidence/session-resume/README.md`); D4 would have to do it again with a
deployment, and a deployment is spend this issue was not authorised to make.

### 2. Idle timeout — `documented, not exercised here`

**Quoted.** 900 s (15 min) by default, 120 s – 14 400 s per agent version; on timeout the platform
deprovisions compute and saves state until the session is referenced again
(`../evidence/platform-behaviour.md` §2, with the caveat that D1 did not wait the eviction out either).

**What this band could observe.** Nothing of it. A `docker stop` is an explicit stop, not an idle
eviction, so even the state-machine half is a different trigger. D2's own evidence
(`../band-d-durable-inside/evidence/restart-across-stop.md`) is the closest capture, and it too is an
explicit `:stop`.

**What would move it.** A session left untouched past its configured `idle_timeout_seconds`, then
referenced again, with the compute reclaimed and the state intact. That is 2 minutes at the minimum
setting and 15 minutes at the default — a wall-clock cost this issue does not pay for a fact the API
publishes as a default.

### 3. Foreground response when the client disappears — `documented, not exercised here`

**Quoted.** Foreground responses stay tied to the client connection: the response resolves
**`cancelled`** with `background: false`, and the in-flight work is not preserved for a later lifetime
(`../evidence/platform-behaviour.md` §3, from a real 5-second abandonment of a 40-second run).

**What this band could observe.** `client-dies-mid-run` kills a client and reads a call count. That
answers **what our application did next** — in the baseline, the same call happened again; in D3, the
step that had not returned happened again too. It does not and cannot say what the platform would have
called the response, because the response was served by a local stand-in.

**What would move it.** Abandon a real foreground run mid-flight against a deployment and read the
Response object's terminal status. One `az rest GET` after the client gives up — which is what D1 did,
and what a D4 that owned a deployment would repeat per fault.

### 4. No replay — `documented, not exercised here`

**Quoted.** No replay for a foreground response. Only *stored background* responses with
`resilient_background` opted in are re-entered, and re-entry **starts at the handler's beginning** — not
deterministic replay, no restored local variables or call stack
(`../evidence/platform-behaviour.md` §4; [recover long-running work](https://learn.microsoft.com/azure/foundry/agents/how-to/recover-long-running-work)).

**What this band could observe.** It measured the *absence of replay* in the one place an application
can observe it: `durable-outside` / `client-dies-mid-run` shows the resumed run re-calling the step that
was in flight, because no memo existed for it. That is consistent with "the platform does not replay the
work", and it is also consistent with a hundred other explanations. Consistency is not reproduction.

**What would move it.** The same deployment as §3, with `store=true` and `background=true` on the
request and `resilient_background` opted in — accepting that this changes the baseline D1–D3 compare
against, which is why D1 declined to do it in the first place.

---

## The rule this file is really stating

A band whose subject is *attribution* fails silently if it lets a local analogue into a platform cell.
The failure is not a wrong number; it is a right number attached to the wrong owner, and it would
misrepresent both Microsoft's platform and this library — one by crediting it with a guarantee it did
not make, the other by taking credit for one it did not need.

So the split is enforced three times over:

1. **In the result format.** `platform` and `application` are separate required blocks; `lib/result.py`
   refuses a document missing either, and refuses `reproduced here` without a live platform call.
2. **In the harness.** The stand-in service's result blocks say what it is not, in the result itself,
   rather than in a README a reader may never open.
3. **In the matrix.** `faults/lib/summarise.py` renders the platform column from `platform` and the
   application column from `application`. There is no code path that fills one from the other.

What a reader should conclude: **the application-side answers here are measurements, and the
platform-side answers are quotations.** A run that had a deployment could turn the second kind into the
first. This one did not, and says so on every row.
