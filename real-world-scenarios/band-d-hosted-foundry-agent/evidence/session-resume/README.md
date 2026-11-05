# Session resume — captures, in the order they were taken

Five files, one per step of the session experiment described in
`../platform-behaviour.md` §5. Timestamps are Unix seconds, exactly as the API
returned them.

| File | What it is | `agent_reference.version` |
| --- | --- | --- |
| `session-before.json` | Session `6f7126f7…` before the stop | — (session object) |
| `session-after-stop.json` | After `POST …:stop` | — |
| `session-after-resume.json` | After the follow-up turn on the same session | — |
| `resume-response.json` | The follow-up Response (completed 2026-11-05T12:48:18Z) | **1** |
| `fresh-session-control.json` | The same follow-up with no `agent_session_id` | **2** |

## Why the control says version 2 and everything else says version 1

`fresh-session-control.json` was taken after a second `deploy.sh` run, which
registers a **new agent version** — the documented behaviour of
`create_version()`, and the reason `deploy.sh` tags images with a timestamp. That
run re-registered `coverage-assessment-agent` as version **2** with the same image
and configuration, so the control hit version 2. The three session objects and
`resume-response.json` predate it and belong to version 1; `session-after-resume`
pins `"agent_version": "1"` explicitly.

The experiment is unaffected: both turns ran the same image and configuration, on
the same agent *name*, and the control's point is that a **new session** has no
prior context — which its session id (`fe7c28e3…`) and its `NO PRIOR CONTEXT`
answer establish independently of the version number. Nothing in
`platform-behaviour.md` relies on the version, and nothing here is a different
agent.

`run-20261105T125254Z/` (the captured prior-auth run) was taken between the two
deploys and is version 1 throughout.
