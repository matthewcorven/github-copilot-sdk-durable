# The middle column: the agent-framework durable extension, run

Band G compares three implementations. The first (`baseline/`, #176) and the third (`durable/`, #177) live in
this repository. This directory is the second, and it is the one both earlier rounds recorded as *not run*.

**That marking was wrong.** G2 and the first draft of G3 said no prerelease `Microsoft.Agents.AI.DurableTask`
package existed on NuGet. It does — 41 prerelease versions, latest `1.16.0-preview.261029.1`, MIT,
`net10.0` — and the probe behind the claim used a malformed feed URL. The package installs, the vendor's own
human-in-the-loop sample runs to completion against the Durable Task Scheduler emulator, and the six Band G
faults run against it. So it is measured, with two residual gaps stated below rather than filled in.

## What is here

| Path | What it is |
| --- | --- |
| `upstream/` | The vendor's sample, byte-identical: `microsoft/agent-framework-durable-extension` @ `3ffe3fb`, `dotnet/samples/DurableWorkflows/ConsoleApps/08_WorkflowHITL/`, MIT. Hashes below. |
| `src/` | The same workflow, driven by verbs instead of by the sample's console loop, so a fault script can put a run in a state, kill a process, and observe from another. |
| `LICENSE` | The vendor's MIT license, as the sample ships it. |

## Pinned source

| Field | Value |
| --- | --- |
| Repository | `microsoft/agent-framework-durable-extension` |
| Commit | `3ffe3fb354b02aba62d482abc829e6014344741f` |
| License | MIT |
| Sample | `dotnet/samples/DurableWorkflows/ConsoleApps/08_WorkflowHITL/` |
| Package | `Microsoft.Agents.AI.DurableTask` `1.16.0-preview.261029.1` |

Local SHA-256 of the vendored files, so a reader can tell a re-fetch from an edit:

```
9656fab610183f944282b8eac228daca6ffbcd6f1b19533c71f7459fd0fb6744  upstream/Program.cs
fbb4167e5358cade66842e503e7c657e213283377b159ec93bd48eaa2d667104  upstream/Executors.cs
4e49d6e8442bd9f5f16c694f4f25e8de8fb5a4fd37d94825996935b140b67bdf  upstream/README.md
```

`src/` is ours: it rebuilds the sample's graph from the same executors (`upstream/Executors.cs` is compiled
in, not copied) and adds the verbs. It changes nothing about the workflow.

## What it takes to run

```bash
docker run -d -p 8080:8080 mcr.microsoft.com/dts/dts-emulator:latest
dotnet build src/ExtensionApp.csproj
export DURABLE_TASK_SCHEDULER_CONNECTION_STRING="Endpoint=http://localhost:8080;TaskHub=default;Authentication=None"

./src/bin/Debug/net10.0/extension-app start    --instance demo --stop ManagerApproval
./src/bin/Debug/net10.0/extension-app status   --instance demo     # Running
./src/bin/Debug/net10.0/extension-app answer   --instance demo --port ManagerApproval --wait
./src/bin/Debug/net10.0/extension-app inspect  --instance demo
```

The connection-string keys are `Endpoint`, `TaskHub` and `Authentication` — **not** the `AccountEndpoint`
the vendor README shows, which fails with `Value cannot be null. (Parameter 'Endpoint')`. That README is
stale against the 1.28.0 client this pins; the working string is above and is what the harness uses.

## What the version that runs changes about the comparison

| Requirement | How it is met here, measured |
| --- | --- |
| Restart/resume | The instance outlives its process. `kill-during-wait`: the run parked at a port, the process serving it killed with SIGKILL, and a different process finished it. |
| No repeated model calls | **Not measurable.** The vendored sample's executors call no model — they build an `ApprovalRequest` from a string and return a fixed string — so there is no call to count and no number is claimed. |
| A decision cannot be overwritten | The answer is an **event** named for the port (`RaiseEventAsync`). A second event for the same port is recorded and schedules no second step; a late event on a terminated run runs no post-approval step. |
| Abort cascades | **Not observable.** Termination is the scheduler's call and the ports live inside one orchestration, so there is no child to observe. What is measured: the terminate is acknowledged and the run does not move (`abort-mid-wait`). |
| Operational weight | The heaviest of the three: a scheduler, or its emulator, is a prerequisite even for one run. Stopping it under a live run fails the client — availability, not state. |

## Where it disagrees with the other two columns, said plainly

- **Its wait is the substrate's.** A process can die and the run continues; that is closer to the Durable
  variant than to the baseline, and it is not this repository's doing.
- **Its decisions are events, not values.** A duplicate is accepted by the scheduler and then inert, rather
  than refused with a reason. The observable is the same — the run advances once — but the mechanism is the
  opposite of the Durable variant's first-writer-wins document, which refuses the write.
- **Terminate did not become a framework terminal status on this emulator.** `abort-mid-wait` records
  `abort acknowledged, run unchanged` with five samples. That is a finding about this substrate version, not
  a claim about the product, and it is left in the result rather than reworded.
