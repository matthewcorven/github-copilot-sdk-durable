# The Durable layer this scenario was proven against

The scenario consumes the layer from the repository, never from a copy:
`agent/Dockerfile` installs `lib/py/durable.py` into the image, and
`infra/deploy.sh` assembles the build context from `lib/py/`.

| | |
| --- | --- |
| Layer commit | `8a27683` — `feat(durable): external events — a task that waits for something outside the store (#160)` |
| Python package | `lib/py/pyproject.toml` `copilot-durable` **0.1.0** |
| C# package | `lib/Copilot.Durable.csproj` `Copilot.Durable` **0.5.0** |
| Copilot SDK | `github-copilot-sdk==1.0.28` (pinned in `agent/requirements.txt`) |
| Hosting stack | `agent-framework-*` with D1's two dependency-range corrections (`../band-d-hosted-foundry-agent/agent/PATCHES.md`) |

## Why the layer change is in this PR and not in a release

`.github/copilot-instructions.md` says a scenario's feature PR does not land while its layer
changes are unpublished: revision → release → scenario pinned to the tag. This branch follows
the *development* half of that rule — it carries the layer change as its own commit and
pins to the layer's source tree — and the landing order is:

1. the layer commit (`8a27683`) merges to `dev`;
2. a release PR cuts the tag and the package versions (the Python package is `0.1.0`, which is
   below the C# package's `0.5.0`; the release is what reconciles them);
3. this scenario merges pinned to that tag, and `agent/Dockerfile` stops copying the source
   tree and installs `copilot-durable==<version>` instead.

Until step 2 has happened, step 3 cannot: there is no published version to pin. The Dockerfile
copying `lib/py/durable.py` is the development form of the install and says so in place. It
is a copy of the *file*, not a fork of the *layer*: the file is the repository's, taken at the
commit under test, and there is no scenario-specific edit in it.

## What "in step" means here

The same semantic change is in C#, TypeScript and Python:

| Concept | C# | TypeScript | Python |
| --- | --- | --- | --- |
| Park on an event | `TaskCommit.WaitingForEvent` | `TaskCommit.waitingForEvent` | `TaskCommit.waiting_for_event` |
| Name namespace | `TaskCommit.IsExternalEvent` / `ExternalEventPrefix` | `TaskCommit.isExternalEvent` / `EXTERNAL_EVENT_PREFIX` | `TaskCommit.is_external_event` / `EXTERNAL_EVENT_PREFIX` |
| Deliver | `DurableStore.RaiseEventAsync` | `DurableStore.raiseEventAsync` | `DurableStore.raise_event_async` |
| Memo key | `DurableStore.ExternalEventMemoKey` | `DurableStore.EXTERNAL_EVENT_MEMO_KEY` | `DurableStore.EXTERNAL_EVENT_MEMO_KEY` |
| A park settles as | `pending` | `pending` | `pending` |
| `WaitingOn` written by both arms | yes | yes | yes |
| Completion per run | yes | yes | yes |

No port was left behind. Three suites were run on the branch: **223 .NET**, **121
TypeScript**, **74 Python**, all green.

## The store is the cross-language contract, and it did not move

The wire format is unchanged: the task record still carries `waitingOn` as a list of strings
and `memos` as an object. This scenario's store was written by the Python port inside the
container and read back by the same port; the .NET test `WaitingForEvent_StopsTheRunAndSurvivesAReopen`
opens the change's JSONL with the C# provider, and the layer's parity suite replays the same
records across ports. A store written before this change holds only child task ids in
`waitingOn` — no name in it starts with `external:` — so it reads exactly as it always did.

## The scenario's own files

| Path | What it is |
| --- | --- |
| `durable/GAP.md` | The gap, the four reproductions, the revision, the compatibility notice |
| `durable/local-restart.sh` | The restart as four processes over one file — no subscription needed |
| `evidence/local-restart/` | That run's output |