# Band B — the Ralph Loop baseline

A port of the vendored Ralph Loop recipe (`upstream/`) to C#, against the plain
GitHub Copilot SDK. The loop reads a plan file, sends a prompt to a fresh session, lets the
agent edit files in a scratch directory, re-reads the plan, and repeats until the plan
reports complete.

**What it is not:** durable. The loop's position is a local variable in a running process.
**If you kill the process mid-iteration, there is no record of which step was running.** The
plan file may have been half-updated; recovery is re-reading the markdown and hoping the last
edit was consistent. That is the property Band B exists to measure — issue #148.

Everything under `baseline/` is one file-based .NET app plus a shell runner. Nothing here
references `lib/Durable.cs`; there is no store, no checkpoint, no task, no admission, and no
conversation document.

---

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0 or later | File-based apps (`dotnet run file.cs`). On this machine `/opt/homebrew/bin/dotnet` is 10.0.402 |
| GitHub Copilot CLI | 1.0.103 or later | The SDK drives the CLI; the SDK ships its own copy if none is on `PATH` |
| GitHub Copilot access | a signed-in CLI | The loop makes live model calls |

`run.sh` prefers `/opt/homebrew/bin/dotnet`, then whatever `dotnet` is first on `PATH`. Set
`DOTNET` to override.

## One command

```bash
./real-world-scenarios/band-b-local-job-agent/run.sh
```

The runner creates the target directory fresh, runs the loop, verifies the result, and
deletes the target on exit. To keep it:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/run.sh
```

To shorten the run for a smoke test, pass the cap as the first argument. The default is the
constant in `baseline/ralph-loop.cs`.

## What the target directory contains

`.scratch/band-b-target/` in the worktree root — never inside this tree, so a run cannot
damage the repo:

| Path | Contents |
| --- | --- |
| `PROMPT.md` | The instruction sent on every iteration |
| `PLAN.md` | The checklist, with `Status: in-progress` |
| `release-notes.md` | Notes whose headings are one level too shallow and whose `New` bullets lack a marker |
| `docs/section-01.md` … `section-10.md` | Ten sections, each missing its H1; two also carry a broken link or a stale date |

Twelve checkable defects, described in `PLAN.md`, one item per line. The
agent edits real files and ticks items as it goes, and only after re-reading the file.

## How the loop proves it did real work

Printing is not enough. After the loop exits, `baseline/verify.sh` checks the target on disk:

- the headings in `release-notes.md` moved exactly one level, the title and the fenced block did not;
- every `New` bullet ends with `<!-- NEW -->` exactly once;
- every `docs/section-*.md` opens with an H1;
- the link and the date in sections 03 and 09 were repointed and replaced;
- `PLAN.md` says `Status: complete` and has no open item.

`run.sh` exits non-zero if any check fails, or if the plan is complete but the files are not.

## Observing progress

A run prints the target path, then one block per iteration with the tool calls the agent made
and the first line of its answer. In a second terminal:

```bash
KEEP=1 ./real-world-scenarios/band-b-local-job-agent/run.sh &
tail -f .scratch/band-b-target/PLAN.md     # watch items get ticked
ls -R .scratch/band-b-target/docs          # watch the sections change
git -C .scratch/band-b-target diff 2>/dev/null || true
```

## The iteration cap

`const int MaxIterations = 10;` at the top of `baseline/ralph-loop.cs`. When the cap is
reached without the plan reporting complete, the loop prints the cap, prints the first open
item, and exits with status 3. A broken loop cannot run forever.

Exit statuses:

| Status | Meaning |
| --- | --- |
| 0 | The plan reported complete and the files match |
| 1 | The target does not match the plan (from `run.sh`) |
| 2 | No `PLAN.md` in the working directory |
| 3 | Iteration cap reached with the plan still open |

## The loop shape

Preserved from `upstream/ralph-loop.ts`:

1. read the plan file once per iteration;
2. if the plan reports complete, stop;
3. otherwise create a **fresh** session (no resume, no context carried over) in the target
   directory, with permission requests approved for unattended operation;
4. send the prompt and wait for the turn to finish;
5. dispose the session and loop.

The SDK bindings are one method, `RunOneIterationAsync`. The control flow above it —
`RalphLoopAsync` — is plain C#, so a port to another language swaps that one method and
leaves the loop visible.

## Layout

```
real-world-scenarios/band-b-local-job-agent/
├── README.md              # this file
├── run.sh                 # one run against a fresh scratch target
├── baseline/
│   ├── ralph-loop.cs      # the port
│   ├── verify.sh          # checks the target after the run
│   └── target/            # the template copied into the scratch target
└── upstream/
    ├── LICENSE            # MIT, github/awesome-copilot
    ├── PROVENANCE.md      # repo, commit, paths, hashes
    ├── ralph-loop.md
    └── ralph-loop.ts
```

## Upstream

`upstream/` is byte-identical to `github/awesome-copilot` at commit
`7e408815ddcb64889a3683799a85399331cfd4ef`, MIT. See `upstream/PROVENANCE.md`.
