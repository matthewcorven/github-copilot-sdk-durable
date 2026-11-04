# Band A — Local dev-tool agent

A Blazor Server application in which each customer enquiry starts a Copilot session that
works the enquiry and streams its progress into the browser. This directory holds the
**baseline**: the Build26 reference application, vendored unmodified and ported to a
buildable project, defects included.

```
upstream/     byte-identical copy of microsoft/Build26-BRK206-… @ da8b33a, MIT
PROVENANCE    pinned SHA, file list, hashes, and the three port changes
baseline/     the same application as a project in this repository
run.sh        builds and starts it, prints the URL
```

The Durable variant of this scenario is a separate work item (#145, #146, #148). Nothing in
`baseline/` refers to it: there is no store, no task, and no checkpoint in this tree.

## Prerequisites

| Requirement | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0 | `net10.0` target; the .NET 10 Blazor toolchain is required |
| GitHub Copilot CLI | signed in | The SDK spawns the runtime over stdio; `copilot` must be on `PATH` and authenticated |

The app needs no Azure subscription and no database server: listings are seeded into a local
SQLite file on first start from `baseline/Data/Properties/*.json`.

Check the sign-in before running:

```bash
copilot --version
```

## Run

```bash
cd real-world-scenarios/band-a-local-dev-tool
./run.sh
```

`run.sh` publishes the project into a scratch directory under `$TMPDIR`, starts it, and
prints:

```
Band A baseline (AgentOrchestrator, unmodified upstream behaviour)
  URL:     http://localhost:5027
  Scratch: /var/folders/…/band-a-baseline.XXXXXX   (KEEP=1 to keep it)
  Stop with Ctrl-C.
```

`KEEP=1 ./run.sh` keeps the scratch directory on exit; otherwise it is removed. Everything
the app writes — `properties.db` and the Copilot client's base directory, which is
`AppContext.BaseDirectory/.copilot` in `AppState.cs` — resolves inside that directory, not in
this repository. `PORT=5028 ./run.sh` changes the port.

## What to click

1. Open the printed URL. The page shows the phase pipeline (Queued → Validating → Searching →
   Writing Report → Rejected / No Matches / Done) and a dashboard counting agents.
2. Click the **+** button at the top right, then pick a sample enquiry — for example *"Looking
   for a 3-bed family home near good schools in the suburbs, budget around $500k"*.
3. The new card shows the enquiry and, as the model works, its reported intent and phase. The
   dashboard's *Processing*, *Completed* and *Rejected* counts follow it. A completed enquiry
   keeps its card, report included, until the process exits.

Enquiries that the model rejects (*"BUY CRYPTO NOW!!! …"*) leave a card that disappears by
itself 15 seconds later. Enquiries that fail with an exception are removed immediately.

## This application loses all agent state on restart

No agent, conversation, enquiry, phase or report is written anywhere. `AppState` keeps them
in an in-process `Dictionary<string, Agent>`, and the process exiting is the end of them. The
SQLite database holds property listings only — it has never held agent state. Restart the
app and the dashboard is empty; the Copilot session created for each enquiry is not resumed,
because its identifier is not retained.

## Known defects (preserved)

These are upstream behaviour and are kept as-is: they are what the Durable variant is being
compared against. They are not fixed here, and no code in `baseline/` compensates for them.

1. **In-memory dictionary.** `AppState.Agents` is a `Dictionary<string, Agent>` in a
   singleton. It lives in one process. Restart, recycle, or a second server, and the
   enquiries are gone.
2. **15-second eviction by timer.** A rejected enquiry is removed by a hard-coded
   `await Task.Delay(15000)` inside the run loop. The delay is part of the request path, so
   the code that removes the agent runs in the same task that ran it; if the process is
   restarted in that window, nothing removes the agent and nothing restarts it either.
3. **No persistence of the SDK session.** `client.CreateSessionAsync(...)` returns a session
   whose id is never stored and never resumed. The runtime may keep session data under the
   client's base directory, but the application cannot find it again.
4. **No thread safety.** `Agents` is written by every `RunAgentAsync` task and read by the
   Blazor renderer without a lock. A `Dictionary<string, Agent>` is not safe for concurrent
   mutation; two enquiries started at the same moment mutate it from two threads.
5. **Unbounded growth for successful enquiries.** Completed agents are kept for the life of
   the process by design ("For demo, successful agents stay on screen forever"), so a
   long-running instance accumulates them.
6. **No cancellation.** Nothing in the application calls `CopilotSession.DisposeAsync` for a
   running enquiry when the browser goes away or the user stops looking at it; the run loop
   is a fire-and-forget `_ = AppState.RunAgentAsync(enquiry)`.

## What changed to make it build

Nothing that a compiler required. The upstream project builds as vendored against .NET
10.0.402 and the package versions it pins (`GitHub.Copilot.SDK` 1.0.0, `Markdig` `*`,
`Microsoft.EntityFrameworkCore.Sqlite` 9.0.0). `baseline/` differs from
`upstream/AgentOrchestrator/` in three places, none behavioural:

| Change | Why |
| --- | --- |
| `<AssemblyName>BandABaseline</AssemblyName>`, `<RootNamespace>AgentOrchestrator</RootNamespace>` | The assembly is no longer named after the upstream application. |
| `Components/App.razor`: `AgentOrchestrator.styles.css` → `BandABaseline.styles.css` | The generated scoped-CSS bundle is named after the assembly; without this the two scoped stylesheets (layout reconnect modal) are not linked. |
| `AgentOrchestrator.sln` and the upstream `.gitignore` not carried over | The project is built by path; the root `.gitignore` covers `bin/` and `obj/`. |

`Agent.cs`, `AppState.cs`, `Program.cs`, `Phase.cs`, `PropertyDatabase.cs` and every Razor
file are unmodified copies.

Two upstream warnings are visible on every build and start: NU1903 for
`SQLitePCLRaw.lib.e_sqlite3` 2.1.10 (a transitive dependency of
`Microsoft.EntityFrameworkCore.Sqlite` 9.0.0) and EF's `KeyFeatures` value-converter
warning. Neither is fixed, because fixing them means changing the pinned packages.

## What was executed

On 2026-11-04, macOS arm64, .NET SDK 10.0.402, GitHub Copilot CLI 1.0.105 signed in:

- `./run.sh` — published, started, served `GET /` with HTTP 200 and the Blazor
  InteractiveServer websocket connected.
- One enquiry submitted through the UI (*"Looking for a 3-bed family home near good schools
  in the suburbs, budget around $500k"*) ran against the live runtime. The card moved
  through Validating → Searching, the tool line *Search matching properties* appeared, and
  the dashboard settled at `Processing 0 / Completed 1 / Rejected 0` — the `Phase.Done`
  path, with the card retained.
- A second enquiry (*"BUY CRYPTO NOW!!! Not actually looking for property lol"*) was rejected
  by the model. `Processing` dropped to 0 and the card disappeared after the upstream
  15-second delay, with no console error.

Not executed here: several enquiries at once (defect 4), a restart mid-enquiry (defect 3),
and a run against a different CLI version. Those belong to the fault-evidence work item.

## Licence

MIT. See `upstream/LICENSE` and `upstream/PROVENANCE.md`.
