# What the container's filesystem does — measured, not assumed

A hosted agent's working state is only as durable as the path it sits on. This file records
what was **measured inside the deployed container**, and separates three questions that are
easy to conflate:

1. which paths are writable;
2. which paths survive a session stop;
3. what the "session volume" actually is, as opposed to what the documentation calls it.

The probes are `d2_tool.py probe` locally and `store_report`'s `container` block in the
deployment. Both print measurements; neither asserts.

---

## 1. Writable paths — measured in the deployed container

From `run-20261105T164956Z/10-ask.tools.txt`, the first turn of the run, before anything was
written:

```json
{
 "/": { "exists": true, "writable": true },
 "/app": { "exists": true, "writable": true },
 "/files": { "exists": false },
 "/home/session": { "exists": true, "writable": true },
 "/tmp": { "exists": true, "writable": true },
 "home": "/home/session",
 "markerPath": "/home/session/d2-marker",
 "storePath": "/home/session/durable/working-state.jsonl",
 "userId": 0
}
```

Three findings, each of which contradicts an assumption that would have been reasonable to
make:

- **`/` is writable.** The image runs as `uid 0`, so "the image is read-only" is not true
  for the root filesystem of *this* container. The same probe run locally against the same
  Dockerfile without the platform returns `OSError: [Errno 30] Read-only file system` for
  `/` — the platform's container is more permissive than a plain `docker run`, not less.
  So a scenario cannot infer writability from the image alone in either direction; it has
  to measure.
- **`/app` is writable.** The code directory is not protected. A store written there would
  work, and would then be lost on the next container (see §2) — which is precisely the
  trap: "the write succeeded" says nothing about whether the write survives.
- **`/files` does not exist.** The platform documentation describes the persisted session
  filesystem as "$HOME and uploaded files" (quoted in
  `../band-d-hosted-foundry-agent/evidence/platform-behaviour.md` §1). On this deployment
  only `$HOME` is mounted; `/files`, which the accelerator's reference material treats as the
  uploaded-file mount, is absent. A scenario that resolved its store under `/files` would have to create
  the directory first — the store does that — and would then be writing outside the session
  volume it believes it is on, so the state would be lost exactly as it would have been in
  `/tmp`.
- **`HOME=/home/session` and `userId=0`.** This is the platform's session volume, root-owned,
  which is why D1's Dockerfile runs as root (a non-root UID gets `EACCES` creating
  `$HOME/.sessions` and the container exits before `/readiness` binds).

## 2. What survives a stop — the same path, a different file

The store path is `/home/session/durable/working-state.jsonl`. The control run
(`control-session-20261105T203511Z/90-control.tools.txt`) opens a **new** session and asks
for the same path:

```json
{
 "bytes": 0,
 "container": { … },
 "documents": [],
 "raw": "",
 "sha256": "no-file",
 "storePath": "/home/session/durable/working-state.jsonl",
 "tasks": [],
 "files": { "directory": "/home/session/durable", "files": [], "pid": 12 }
}
```

The directory exists — the process just created it — and holds **nothing**. The path is
identical and the contents are not there, because a fresh session has a fresh volume.

Against that, the same path in the **stopped and resumed** session
(`run-20261105T164956Z/40-resume.tools.txt`) holds the task, the assessment and the deadline
the previous container wrote. The pair is the whole argument:

| | control (fresh session) | resumed session |
| --- | --- | --- |
| store path | `/home/session/durable/working-state.jsonl` | `/home/session/durable/working-state.jsonl` |
| file | absent (`sha256: "no-file"`) | 5896 bytes, 25 records |
| task `t4` | absent | `Terminal`, checkpoint `await`, memo `external-event` |
| assessment | absent | `Coverage criteria are met.` |

**`$HOME` is per-session, not per-container.** The store survives a stop because the same
session is resumed; it does not survive a *different* session. That distinction is the one
the issue asked for and it is not a detail: a scenario that claimed "the state is on a
volume" without a fresh-session control would be claiming survival of something else.

## 3. `/tmp` — ephemeral, and not used

Nothing in this scenario writes to `/tmp` at runtime. The one use was the **image build**,
which unpacked `lib/py` into `/tmp/durable-package/` and installed from it — that is
build-time, inside `RUN`, and leaves nothing in the running container.

The store deliberately does not live under `/tmp`: on a hosted agent the only documented
persistence is the session volume at `$HOME`, and `/tmp` is neither mounted nor promised.
The `container` block reports `/tmp` writable in this container, which is exactly why a
store there would *appear* to work and then disappear — the failure mode this band exists to
rule out.

## 4. The lock sidecar — and what a restart looks like in it

The Durable JSONL store holds an exclusive lock for its lifetime, as the sidecar
`{path}.lock`. `store_report`'s `files` block lists the directory, so the restart is visible
there:

```
ask     (session's first turn)   files: [ working-state.jsonl.lock 38 @ 20:34:02 ], pid 5
                                 ← the open takes the lock; the store file is not created yet
assess  (after the writes)       the store file appears and the documents grow
stop    (1793910857)             status idle
resume  (new container)          same path, same task `t4`, same deadline
decide  (final)                  files: [ working-state.jsonl 5898 @ 20:34:36,
                                          working-state.jsonl.lock 38 @ 20:34:25 ], pid 5
```

The pid is **5** in the first turn, in the last, and in the control's separate session: it is
the *image's* entry point, not a process id that distinguishes containers, so it is **not**
evidence of a restart. An earlier capture of this scenario showed two different pids across
the stop, which was read as the restart; it was not, and this paragraph replaces that claim.

What does move is the lock's mtime — 20:34:02 when the first container opened the store,
20:34:25 when the resumed one did — and that, with the `last_accessed_at` on the session
object, is where the restart is visible. Neither is on its own: the mtime says a process
opened the store again, and the session object says the platform resumed the same session.

Two things worth recording:

- The lock's presence at the **first** turn, before any document existed, is the store's
  open: `DurableStore.create` takes the lock even when the file does not exist yet, so an
  empty store is a locked store.
- **No stale lock blocked the restart.** The lock is released by the operating system when
  the process dies (`FileShare.None` on the file handle), so the resumed container acquired
  it without special handling. This was the one thing most likely to break a restart in this
  deployment, and it did not — on macOS in CI and on the platform's Linux container alike.
  The `.lock` file itself is not deleted on release in every path, but a leftover file is
  not a held lock; the handle is.

## 5. Build-time findings that cost a deploy each

| Symptom | Cause | Fix landed |
| --- | --- | --- |
| `pip install /tmp/durable-package` → `BackendUnavailable: Cannot import 'setuptools.build_meta'` | `lib/py/pyproject.toml` declares a setuptools build backend the `python:3.12-slim` base does not carry, and `--no-build-isolation` needs it present | The image copies `durable.py` into `site-packages` instead — the same install without a build backend. `requirements.txt` already pins the layer's only runtime dependency (`github-copilot-sdk`). |
| `Function failed.` on `store_report`, silently, on an empty store | The tool read a file that had not been created and the exception surfaced to the model as a bare failure | `store_report` reports `sha256: "no-file"`, `bytes: 0`, `raw: ""` — an empty store is a state to report, not an error |
| `az rest … create_version` → `server_error` 500 twice in a row | Capability hosts exist but the project's agent backend had not finished binding when the version was registered | Retried after the hosts settled; the third attempt succeeded. Not a code change; recorded because a reader will hit it |

## 6. What was not measured

- **Disk quota.** The session volume's size is not published and was not probed; a store
  that outgrew it would fail at a write, and this scenario writes kilobytes.
- **`$HOME` behaviour after the 30-day session expiry.** D1 measured the window; nothing here
  waited for it.
- **A non-root UID.** D1 established that a non-root user cannot start at all (EACCES on
  `$HOME/.sessions`), so every probe here runs as root, which is the only configuration the
  platform will run.
- **Concurrency.** Two containers against one volume is not a state the platform offers; the
  local two-process case is `../durable/local-restart.sh`, which runs its four processes one
  after another for that reason.